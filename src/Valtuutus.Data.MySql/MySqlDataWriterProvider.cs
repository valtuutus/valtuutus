using System.Collections.Concurrent;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql;

public class MySqlDataWriterProvider : IDbDataWriterProvider
{
    // One multi-row INSERT carries at most this many rows.
    private const int RowsPerInsert = 1000;

    // MariaDB's default max_allowed_packet, the smaller default of the two servers.
    private const int DefaultMaxAllowedPacket = 16 * 1024 * 1024;

    // An attribute INSERT closes before a row whose value would take the statement's value text past
    // this many UTF-8 bytes, so a statement holds more only when a single value is larger by itself.
    // Escaping at most doubles a value's text, so a statement within the cap sends at most half of
    // DefaultMaxAllowedPacket in values, leaving the rest for its keys and statement text.
    private const int ValueBytesPerInsert = DefaultMaxAllowedPacket / 4;

    private const int RelationColumnCount = 6;
    private const int AttributeColumnCount = 4;

    // Parameter names of a multi-row INSERT, @p{row}_{col}, shared by every statement shape.
    private static readonly string[][] RowParameterNames = Enumerable.Range(0, RowsPerInsert)
        .Select(row => Enumerable.Range(0, RelationColumnCount).Select(col => $"@p{row}_{col}").ToArray())
        .ToArray();

    // The longest value each key field may hold. The character limits are the ones every provider
    // shares. The byte limits are the widths of the VARBINARY columns: an id within its character
    // limit always fits its column, a type or name only when its UTF-8 form does.
    private readonly record struct KeyLimit(int Characters, int Bytes);

    private static readonly KeyLimit TypeLimit = new(256, MySqlSql.TypeBytes);
    private static readonly KeyLimit IdLimit = new(64, MySqlSql.IdBytes);
    private static readonly KeyLimit NameLimit = new(64, MySqlSql.NameBytes);

    // The columns a relation delete filter can set, in null-mask bit order, with the byte width of
    // each one's table column.
    private static readonly (string Column, int Bytes)[] RelationFilterColumns =
    [
        ("entity_type", MySqlSql.TypeBytes),
        ("entity_id", MySqlSql.IdBytes),
        ("relation", MySqlSql.NameBytes),
        ("subject_type", MySqlSql.TypeBytes),
        ("subject_id", MySqlSql.IdBytes),
        ("subject_relation", MySqlSql.NameBytes),
    ];

    private static string JsonColumn(string column, int bytes) =>
        $"{MySqlSql.JsonColumn(column, bytes)} PATH '$.{column}'";

    private static readonly string EntityTypeColumn = JsonColumn("entity_type", MySqlSql.TypeBytes);
    private static readonly string EntityIdColumn = JsonColumn("entity_id", MySqlSql.IdBytes);
    private static readonly string AttributeColumn = JsonColumn("attribute", MySqlSql.NameBytes);

    private sealed record WriterCommands
    {
        public required string InsertTransaction { get; init; }
        public required string InsertRelationsHead { get; init; }
        public required string InsertRelationsFull { get; init; }
        public required string InsertAttributesHead { get; init; }
        public required string InsertAttributesFull { get; init; }
        public required string TombstoneAttributes { get; init; }

        // Indexed by the null-mask of a relation filter, see RelationFilterMask.
        public required string[] DeleteRelations { get; init; }
        public required string DeleteAttributesOfEntity { get; init; }
        public required string DeleteAttributesByName { get; init; }
    }

    private static readonly ConcurrentDictionary<DbQueryCacheKey, WriterCommands> CommandCache = new();

    private readonly DbConnectionFactory _factory;
    private readonly IServiceProvider _provider;
    private readonly ValtuutusDataOptions _options;
    private readonly string _dbSystem;
    private readonly WriterCommands _c;

    public MySqlDataWriterProvider(DbConnectionFactory factory,
        IServiceProvider provider,
        ValtuutusDataOptions options,
        ValtuutusMySqlOptions dbOptions)
    {
        _factory = factory;
        _provider = provider;
        _options = options;
        _dbSystem = MySqlConnections.DbSystem(dbOptions.ServerType);
        _c = CommandCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static (_, o) => BuildCommands(o), dbOptions);
    }

    private static WriterCommands BuildCommands(ValtuutusMySqlOptions o)
    {
        var rt = MySqlConnections.Relations(o);
        var at = MySqlConnections.Attributes(o);
        var tx = MySqlConnections.Transactions(o);

        var insertRelationsHead =
            $"INSERT INTO {rt} (entity_type, entity_id, relation, subject_type, subject_id, subject_relation, created_tx_id) VALUES ";
        var insertAttributesHead =
            $"INSERT INTO {at} (entity_type, entity_id, attribute, value, created_tx_id) VALUES ";

        var deleteRelations = new string[1 << RelationFilterColumns.Length];
        for (var mask = 0; mask < deleteRelations.Length; mask++)
            deleteRelations[mask] = BuildDeleteRelations(rt, mask);

        return new WriterCommands
        {
            InsertTransaction = $"INSERT INTO {tx} (id, created_at) VALUES (@id, @created_at)",
            InsertRelationsHead = insertRelationsHead,
            InsertRelationsFull = BuildInsert(insertRelationsHead, RelationColumnCount, RowsPerInsert),
            InsertAttributesHead = insertAttributesHead,
            InsertAttributesFull = BuildInsert(insertAttributesHead, AttributeColumnCount, RowsPerInsert),
            TombstoneAttributes = MySqlSql.Compact($"""
                UPDATE {at} a
                JOIN JSON_TABLE(@keys, '$[*]' COLUMNS ({EntityTypeColumn}, {AttributeColumn}, {EntityIdColumn})) k
                  ON a.entity_type = k.entity_type AND a.attribute = k.attribute AND a.entity_id = k.entity_id
                SET a.deleted_tx_id = @tx
                WHERE a.deleted_tx_id IS NULL
                """),
            DeleteRelations = deleteRelations,
            DeleteAttributesOfEntity = MySqlSql.Compact($"""
                UPDATE {at} a
                JOIN JSON_TABLE(@filters, '$[*]' COLUMNS ({EntityTypeColumn}, {EntityIdColumn})) f
                  ON a.entity_type = f.entity_type AND a.entity_id = f.entity_id
                SET a.deleted_tx_id = @tx
                WHERE a.deleted_tx_id IS NULL
                """),
            DeleteAttributesByName = MySqlSql.Compact($"""
                UPDATE {at} a
                JOIN JSON_TABLE(@filters, '$[*]' COLUMNS ({EntityTypeColumn}, {EntityIdColumn}, {AttributeColumn})) f
                  ON a.entity_type = f.entity_type AND a.entity_id = f.entity_id AND a.attribute = f.attribute
                SET a.deleted_tx_id = @tx
                WHERE a.deleted_tx_id IS NULL
                """),
        };
    }

    // Each set filter field becomes an equality join on its column and unset fields are left out of
    // the statement, so every comparison in it can use an index.
    private static string BuildDeleteRelations(string relationsTable, int mask)
    {
        if (mask == 0)
            return $"UPDATE {relationsTable} r SET r.deleted_tx_id = @tx WHERE r.deleted_tx_id IS NULL";

        var columns = new List<string>();
        var on = new List<string>();
        for (var bit = 0; bit < RelationFilterColumns.Length; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            var (column, bytes) = RelationFilterColumns[bit];
            columns.Add(JsonColumn(column, bytes));
            on.Add($"r.{column} = f.{column}");
        }

        return $"UPDATE {relationsTable} r JOIN JSON_TABLE(@filters, '$[*]' COLUMNS ({string.Join(", ", columns)})) f " +
               $"ON {string.Join(" AND ", on)} SET r.deleted_tx_id = @tx WHERE r.deleted_tx_id IS NULL";
    }

    // The text depends only on the row count; every row's created_tx_id is the one @tx.
    private static string BuildInsert(string head, int columns, int rows)
    {
        var sb = new StringBuilder(head, head.Length + rows * columns * 12);
        for (var row = 0; row < rows; row++)
        {
            sb.Append(row == 0 ? "(" : ", (");
            for (var col = 0; col < columns; col++)
                sb.Append(RowParameterNames[row][col]).Append(", ");
            sb.Append("@tx)");
        }
        return sb.ToString();
    }

    public async Task<SnapToken> Write(
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        await using var db = (MySqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Write(db, relations, attributes, ct);
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        using var span = ValtuutusTelemetry.StartWrite(_dbSystem);
        try
        {
            await using var transaction = await ((MySqlConnection)connection).BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var snapToken = await WriteCore(connection, transaction, span.CountRelations(relations), span.CountAttributes(attributes), ct);
            await transaction.CommitAsync(ct);
            span.SetSnapToken(snapToken);
            await NotifyCommitted(snapToken);

            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IDbTransaction transaction,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    )
    {
        using var span = ValtuutusTelemetry.StartWrite(_dbSystem);
        try
        {
            var snapToken = await WriteCore(connection, transaction, span.CountRelations(relations), span.CountAttributes(attributes), ct);
            span.SetSnapToken(snapToken);
            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    private async Task<SnapToken> WriteCore(
        IDbConnection connection,
        IDbTransaction transaction,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        var mySqlConnection = (MySqlConnection)connection;
        var mySqlTransaction = (MySqlTransaction)transaction;

        // Checked before anything is written, so a rejected write leaves nothing behind in the
        // caller's transaction.
        var relationList = ToCheckedList(relations);
        var attributeList = ToDistinctKeyList(attributes);

        var tx = Ulid.NewUlid().ToString();

        await InsertTransaction(mySqlConnection, mySqlTransaction, tx, ct);
        await WriteRelations(mySqlConnection, mySqlTransaction, tx, relationList, ct);
        await WriteAttributes(mySqlConnection, mySqlTransaction, tx, attributeList, ct);

        return new SnapToken(tx);
    }

    // A field over its limit is rejected: a column would either refuse it or keep only a prefix of
    // it, and a stored prefix would be the key of a different tuple.
    private static List<RelationTuple> ToCheckedList(IEnumerable<RelationTuple> relations)
    {
        var list = relations.TryGetNonEnumeratedCount(out var count) ? new List<RelationTuple>(count) : [];
        foreach (var relation in relations)
        {
            EnsureFits(relation.EntityType, TypeLimit, "entity type", relation);
            EnsureFits(relation.EntityId, IdLimit, "entity id", relation);
            EnsureFits(relation.Relation, NameLimit, "relation", relation);
            EnsureFits(relation.SubjectType, TypeLimit, "subject type", relation);
            EnsureFits(relation.SubjectId, IdLimit, "subject id", relation);
            EnsureFits(relation.SubjectRelation, NameLimit, "subject relation", relation);
            list.Add(relation);
        }
        return list;
    }

    // A key written twice in one write would need two live rows, which the unique key forbids, and
    // neither value could be preferred over the other.
    private static List<AttributeTuple> ToDistinctKeyList(IEnumerable<AttributeTuple> attributes)
    {
        var list = new List<AttributeTuple>();
        var keys = new HashSet<(string EntityType, string EntityId, string Attribute)>();
        foreach (var attribute in attributes)
        {
            EnsureFits(attribute.EntityType, TypeLimit, "entity type", attribute);
            EnsureFits(attribute.EntityId, IdLimit, "entity id", attribute);
            EnsureFits(attribute.Attribute, NameLimit, "attribute", attribute);
            if (!keys.Add((attribute.EntityType, attribute.EntityId, attribute.Attribute)))
                throw new InvalidOperationException(
                    $"The write sets attribute '{attribute.Attribute}' of {attribute.EntityType}:{attribute.EntityId} more than once.");
            list.Add(attribute);
        }
        return list;
    }

    private async Task WriteRelations(MySqlConnection connection, MySqlTransaction transaction, string tx,
        List<RelationTuple> relations, CancellationToken ct)
    {
        var chunk = new List<RelationTuple>(RowsPerInsert);
        foreach (var relation in relations)
        {
            chunk.Add(relation);
            if (chunk.Count == RowsPerInsert)
            {
                await InsertRelationChunk(connection, transaction, tx, chunk, ct);
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
            await InsertRelationChunk(connection, transaction, tx, chunk, ct);
    }

    private async Task InsertRelationChunk(MySqlConnection connection, MySqlTransaction transaction, string tx,
        List<RelationTuple> chunk, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = chunk.Count == RowsPerInsert
            ? _c.InsertRelationsFull
            : BuildInsert(_c.InsertRelationsHead, RelationColumnCount, chunk.Count);
        var parameters = command.Parameters;
        for (var row = 0; row < chunk.Count; row++)
        {
            var names = RowParameterNames[row];
            var relation = chunk[row];
            AddValue(parameters, names[0], relation.EntityType);
            AddValue(parameters, names[1], relation.EntityId);
            AddValue(parameters, names[2], relation.Relation);
            AddValue(parameters, names[3], relation.SubjectType);
            AddValue(parameters, names[4], relation.SubjectId);
            AddValue(parameters, names[5], relation.SubjectRelation);
        }
        AddTransactionId(parameters, "@tx", tx);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task WriteAttributes(MySqlConnection connection, MySqlTransaction transaction, string tx,
        List<AttributeTuple> attributes, CancellationToken ct)
    {
        if (attributes.Count == 0) return;

        var keys = new MySqlAttributeKey[attributes.Count];
        for (var i = 0; i < attributes.Count; i++)
            keys[i] = new MySqlAttributeKey(attributes[i].EntityType, attributes[i].Attribute, attributes[i].EntityId);

        await using (var tombstone = connection.CreateCommand())
        {
            tombstone.Transaction = transaction;
            tombstone.CommandText = _c.TombstoneAttributes;
            tombstone.Parameters.Add(new MySqlParameter("@keys", MySqlDbType.JSON)
            {
                Value = JsonSerializer.Serialize(keys, MySqlWriterJsonContext.Default.MySqlAttributeKeyArray)
            });
            AddTransactionId(tombstone.Parameters, "@tx", tx);
            await tombstone.ExecuteNonQueryAsync(ct);
        }

        var values = new string[attributes.Count];
        var start = 0;
        var valueBytes = 0;
        for (var i = 0; i < attributes.Count; i++)
        {
            values[i] = attributes[i].Value.ToJsonString();
            var bytes = Encoding.UTF8.GetByteCount(values[i]);
            var rows = i - start;
            if (rows == RowsPerInsert || (rows > 0 && valueBytes + bytes > ValueBytesPerInsert))
            {
                await InsertAttributeChunk(connection, transaction, tx, attributes, values, start, rows, ct);
                start = i;
                valueBytes = 0;
            }
            valueBytes += bytes;
        }
        await InsertAttributeChunk(connection, transaction, tx, attributes, values, start, attributes.Count - start, ct);
    }

    private async Task InsertAttributeChunk(MySqlConnection connection, MySqlTransaction transaction, string tx,
        List<AttributeTuple> attributes, string[] values, int start, int rows, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = rows == RowsPerInsert
            ? _c.InsertAttributesFull
            : BuildInsert(_c.InsertAttributesHead, AttributeColumnCount, rows);
        var parameters = command.Parameters;
        for (var row = 0; row < rows; row++)
        {
            var names = RowParameterNames[row];
            var attribute = attributes[start + row];
            AddValue(parameters, names[0], attribute.EntityType);
            AddValue(parameters, names[1], attribute.EntityId);
            AddValue(parameters, names[2], attribute.Attribute);
            parameters.Add(new MySqlParameter(names[3], MySqlDbType.LongText) { Value = values[start + row] });
        }
        AddTransactionId(parameters, "@tx", tx);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertTransaction(MySqlConnection connection, MySqlTransaction transaction, string tx,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _c.InsertTransaction;
        AddTransactionId(command.Parameters, "@id", tx);
        command.Parameters.Add(new MySqlParameter("@created_at", MySqlDbType.DateTime) { Value = DateTime.UtcNow });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SnapToken> Delete(
        DeleteFilter filter,
        CancellationToken ct
    ) {
        await using var db = (MySqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Delete(db, filter, ct);
    }

    public async Task<SnapToken> Delete(
        IDbConnection connection,
        DeleteFilter filter,
        CancellationToken ct
    ) {
        using var span = ValtuutusTelemetry.StartWrite(_dbSystem);
        try
        {
            await using var transaction = await ((MySqlConnection)connection).BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var snapToken = await DeleteCore(connection, transaction, filter, ct);
            await transaction.CommitAsync(ct);
            span.SetSnapToken(snapToken);
            await NotifyCommitted(snapToken);

            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    public async Task<SnapToken> Delete(
        IDbConnection connection,
        IDbTransaction transaction,
        DeleteFilter filter,
        CancellationToken ct
    )
    {
        using var span = ValtuutusTelemetry.StartWrite(_dbSystem);
        try
        {
            var snapToken = await DeleteCore(connection, transaction, filter, ct);
            span.SetSnapToken(snapToken);
            return snapToken;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    private async Task<SnapToken> DeleteCore(
        IDbConnection connection,
        IDbTransaction transaction,
        DeleteFilter filter,
        CancellationToken ct
    ) {
        var mySqlConnection = (MySqlConnection)connection;
        var mySqlTransaction = (MySqlTransaction)transaction;
        var tx = Ulid.NewUlid().ToString();

        foreach (var group in filter.Relations.Where(FitsColumns).GroupBy(RelationFilterMask))
        {
            var filtersJson = JsonSerializer.Serialize(group.ToArray(), DeleteFilterJsonContext.Default.DeleteRelationsFilterArray);
            await ExecuteDelete(mySqlConnection, mySqlTransaction, _c.DeleteRelations[group.Key], tx, filtersJson, ct);
        }

        foreach (var group in filter.Attributes.Where(FitsColumns).GroupBy(static f => f.Attribute is not null))
        {
            var filtersJson = JsonSerializer.Serialize(group.ToArray(), DeleteFilterJsonContext.Default.DeleteAttributesFilterArray);
            var sql = group.Key ? _c.DeleteAttributesByName : _c.DeleteAttributesOfEntity;
            await ExecuteDelete(mySqlConnection, mySqlTransaction, sql, tx, filtersJson, ct);
        }

        await InsertTransaction(mySqlConnection, mySqlTransaction, tx, ct);
        return new SnapToken(tx);
    }

    // A field is set when it is non-null. An empty string is a value, so SubjectRelation = ""
    // matches only direct tuples.
    private static int RelationFilterMask(DeleteRelationsFilter f) =>
        (f.EntityType is not null ? 1 << 0 : 0)
        | (f.EntityId is not null ? 1 << 1 : 0)
        | (f.Relation is not null ? 1 << 2 : 0)
        | (f.SubjectType is not null ? 1 << 3 : 0)
        | (f.SubjectId is not null ? 1 << 4 : 0)
        | (f.SubjectRelation is not null ? 1 << 5 : 0);

    // A value over its limit was never written, so its filter matches no row and is dropped.
    private static bool FitsColumns(DeleteRelationsFilter f) =>
        Fits(f.EntityType, TypeLimit) && Fits(f.EntityId, IdLimit) && Fits(f.Relation, NameLimit)
        && Fits(f.SubjectType, TypeLimit) && Fits(f.SubjectId, IdLimit) && Fits(f.SubjectRelation, NameLimit);

    private static bool FitsColumns(DeleteAttributesFilter f) =>
        Fits(f.EntityType, TypeLimit) && Fits(f.EntityId, IdLimit) && Fits(f.Attribute, NameLimit);

    // A UTF-8 form within the character limit holds at most that many characters, so the characters
    // are counted only when it is longer.
    private static bool Fits(string? value, KeyLimit limit)
    {
        if (value is null)
            return true;
        var bytes = Encoding.UTF8.GetByteCount(value);
        return bytes <= limit.Bytes
               && (bytes <= limit.Characters || value.EnumerateRunes().Count() <= limit.Characters);
    }

    private static void EnsureFits(string value, KeyLimit limit, string field, RelationTuple relation)
    {
        if (!Fits(value, limit))
            throw OverLong(field, limit,
                $"relation {relation.EntityType}:{relation.EntityId}#{relation.Relation}@{relation.SubjectType}:{relation.SubjectId}#{relation.SubjectRelation}");
    }

    private static void EnsureFits(string value, KeyLimit limit, string field, AttributeTuple attribute)
    {
        if (!Fits(value, limit))
            throw OverLong(field, limit, $"attribute '{attribute.Attribute}' of {attribute.EntityType}:{attribute.EntityId}");
    }

    private static ArgumentException OverLong(string field, KeyLimit limit, string tuple) =>
        new(limit.Bytes < limit.Characters * 4
            ? $"The {field} of {tuple} is longer than {limit.Characters} characters or {limit.Bytes} UTF-8 bytes."
            : $"The {field} of {tuple} is longer than {limit.Characters} characters.");

    /// <inheritdoc />
    public Task NotifyCommitted(SnapToken snapToken)
        => _options.NotifyDataWritten(_provider, snapToken);

    private static async Task ExecuteDelete(MySqlConnection connection, MySqlTransaction transaction,
        string sql, string tx, string filtersJson, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddTransactionId(command.Parameters, "@tx", tx);
        command.Parameters.Add(new MySqlParameter("@filters", MySqlDbType.JSON) { Value = filtersJson });
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddValue(MySqlParameterCollection parameters, string name, string value) =>
        parameters.Add(new MySqlParameter(name, MySqlDbType.VarChar) { Value = value });

    private static void AddTransactionId(MySqlParameterCollection parameters, string name, string tx) =>
        parameters.Add(new MySqlParameter(name, MySqlDbType.String) { Size = 26, Value = tx });
}

internal sealed record MySqlAttributeKey(string EntityType, string Attribute, string EntityId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MySqlAttributeKey[]))]
internal partial class MySqlWriterJsonContext : JsonSerializerContext;
