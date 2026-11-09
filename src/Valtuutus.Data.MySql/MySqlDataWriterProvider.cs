using Microsoft.Extensions.DependencyInjection;
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

    // Parameter names of a multi-row INSERT, p{row}_{col}, shared by every statement shape. The
    // statement text reads each as @p{row}_{col}.
    private static readonly string[][] RowParameterNames = Enumerable.Range(0, RowsPerInsert)
        .Select(row => Enumerable.Range(0, RelationColumnCount).Select(col => $"p{row}_{col}").ToArray())
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
        public required string WaitUntilSettled { get; init; }
        public required MySqlStatement InsertTransaction { get; init; }
        public required MySqlStatement Register { get; init; }
        public required string InsertRelationsHead { get; init; }
        public required MySqlStatement InsertRelationsFull { get; init; }
        public required string InsertAttributesHead { get; init; }
        public required MySqlStatement InsertAttributesFull { get; init; }
        public required MySqlStatement TombstoneAttributes { get; init; }

        // Indexed by the null-mask of a relation filter, see RelationFilterMask.
        public required MySqlStatement[] DeleteRelations { get; init; }
        public required MySqlStatement DeleteAttributesOfEntity { get; init; }
        public required MySqlStatement DeleteAttributesByName { get; init; }
    }

    private static readonly ConcurrentDictionary<DbQueryCacheKey, WriterCommands> CommandCache = new();

    private readonly DbConnectionFactory _factory;
    private readonly IServiceProvider _provider;
    private readonly ValtuutusDataOptions _options;
    private readonly TimeProvider _timeProvider;
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
        _timeProvider = provider.GetService<TimeProvider>() ?? TimeProvider.System;
        _dbSystem = MySqlConnections.DbSystem(dbOptions.ServerType);
        _c = CommandCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static (_, o) => BuildCommands(o), dbOptions);
        // MySqlConnector drops the password from ConnectionString once a connection opens, so the
        // string is read from a fresh connection that is never opened.
        using var probe = (MySqlConnection)factory();
        _dataSource = MySqlConnections.GetOrCreateDataSource(probe.ConnectionString);
        MySqlPendingSweeper.Ensure(_dataSource, MySqlConnections.Pending(dbOptions));
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

        var deleteRelations = new MySqlStatement[RelationFilterShape.Count];
        for (var mask = 0; mask < deleteRelations.Length; mask++)
            deleteRelations[mask] = MySqlStatement.Compile(BuildDeleteRelations(rt, mask));

        return new WriterCommands
        {
            // Waits for the slot lock of every older write still open: a write holds its slot row
            // locked from its first statement until it ends. Then reads the latest settled token.
            WaitUntilSettled = $"""
                SET SESSION innodb_lock_wait_timeout = @timeout_s;
                SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                START TRANSACTION;
                SELECT slot FROM {MySqlConnections.Pending(o)} WHERE lower_bound < @snap_token LOCK IN SHARE MODE;
                COMMIT;
                {MySqlQueries.Get(o).GetLatestSnapToken.Sql};
                """,
            // The write frees its slot in its own transaction, first thing, so the slot stays locked
            // for the life of the write and is free exactly when the write commits, whoever commits
            // it.
            InsertTransaction = MySqlStatement.Compile(
                $"UPDATE {MySqlConnections.Pending(o)} SET lower_bound = '~~~~~~~~~~~~~~~~~~~~~~~~~~' WHERE slot = @slot AND lower_bound = @lower_bound; INSERT INTO {tx} (id, created_at) VALUES (@id, @created_at)"),
            // Claims a free slot, or adds one, in its own committed transaction; read committed takes
            // no gap locks, so claims don't wait for each other. The newest committed id is read after
            // the claim committed.
            Register = MySqlStatement.Compile($"""
                SET @valtuutus_slot = NULL;
                SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
                START TRANSACTION;
                SELECT slot INTO @valtuutus_slot FROM {MySqlConnections.Pending(o)} WHERE lower_bound = '~~~~~~~~~~~~~~~~~~~~~~~~~~' LIMIT 1 FOR UPDATE SKIP LOCKED;
                UPDATE {MySqlConnections.Pending(o)} SET lower_bound = @lower_bound, claimed_at = UTC_TIMESTAMP(6) WHERE slot = @valtuutus_slot;
                INSERT INTO {MySqlConnections.Pending(o)} (lower_bound, claimed_at) SELECT @lower_bound, UTC_TIMESTAMP(6) FROM DUAL WHERE @valtuutus_slot IS NULL;
                SET @valtuutus_slot = COALESCE(@valtuutus_slot, LAST_INSERT_ID());
                COMMIT;
                SELECT @valtuutus_slot, (SELECT id FROM {tx} ORDER BY id DESC LIMIT 1);
                """),
            InsertRelationsHead = insertRelationsHead,
            InsertRelationsFull = BuildInsert(insertRelationsHead, RelationColumnCount, RowsPerInsert),
            InsertAttributesHead = insertAttributesHead,
            InsertAttributesFull = BuildInsert(insertAttributesHead, AttributeColumnCount, RowsPerInsert),
            TombstoneAttributes = MySqlQueries.Statement($"""
                UPDATE {at} a
                JOIN JSON_TABLE(@keys, '$[*]' COLUMNS ({EntityTypeColumn}, {AttributeColumn}, {EntityIdColumn})) k
                  ON a.entity_type = k.entity_type AND a.attribute = k.attribute AND a.entity_id = k.entity_id
                SET a.deleted_tx_id = @tx
                WHERE a.deleted_tx_id IS NULL
                """),
            DeleteRelations = deleteRelations,
            DeleteAttributesOfEntity = MySqlQueries.Statement($"""
                UPDATE {at} a
                JOIN JSON_TABLE(@filters, '$[*]' COLUMNS ({EntityTypeColumn}, {EntityIdColumn})) f
                  ON a.entity_type = f.entity_type AND a.entity_id = f.entity_id
                SET a.deleted_tx_id = @tx
                WHERE a.deleted_tx_id IS NULL
                """),
            DeleteAttributesByName = MySqlQueries.Statement($"""
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

        return $"UPDATE {DeleteHint(mask)} {relationsTable} r JOIN JSON_TABLE(@filters, '$[*]' COLUMNS ({string.Join(", ", columns)})) f " +
               $"ON {string.Join(" AND ", on)} SET r.deleted_tx_id = @tx WHERE r.deleted_tx_id IS NULL";
    }

    // MySQL may scan the whole table instead of seeking an index when an entity's tuples are mostly
    // tombstones. A scan locks every row it reads, and inside a caller's repeatable-read transaction
    // it keeps them: deletes of unrelated entities then wait on each other and deadlock. The hint
    // reads the filters first and seeks the index the shape leads with. An index that doesn't exist
    // only warns; MariaDB reads the hint as a comment and seeks the index on its own.
    private static string DeleteHint(int mask)
    {
        static bool Has(int mask, params int[] bits) => bits.All(b => (mask & (1 << b)) != 0);
        const int entityType = 0, entityId = 1, relation = 2, subjectType = 3, subjectId = 4;
        var index = Has(mask, entityType, entityId) ? " INDEX(r idx_tuples_entity)"
            : Has(mask, entityType, relation, subjectType, subjectId) ? " INDEX(r idx_tuples_subject)"
            : "";
        return $"/*+ JOIN_ORDER(f, r){index} */";
    }

    // The text depends only on the row count; every row's created_tx_id is the one @tx. A full chunk's
    // statement is compiled once, with the catalog; a shorter last chunk's once per write.
    private static MySqlStatement BuildInsert(string head, int columns, int rows)
    {
        var sb = new StringBuilder(head, head.Length + rows * columns * 12);
        for (var row = 0; row < rows; row++)
        {
            sb.Append(row == 0 ? "(" : ", (");
            for (var col = 0; col < columns; col++)
                sb.Append('@').Append(RowParameterNames[row][col]).Append(", ");
            sb.Append("@tx)");
        }
        return MySqlStatement.Compile(sb.ToString());
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

        var now = _timeProvider.GetUtcNow();
        var (tx, slot) = await Register(ct);

        await InsertTransaction(mySqlConnection, mySqlTransaction, tx, slot, now, ct);
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
        var statement = chunk.Count == RowsPerInsert
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
        AddTransactionId(parameters, "tx", tx);
        statement.ApplyTo(command);
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
            tombstone.Parameters.Add(new MySqlParameter("keys", MySqlDbType.JSON)
            {
                Value = JsonSerializer.Serialize(keys, MySqlWriterJsonContext.Default.MySqlAttributeKeyArray)
            });
            AddTransactionId(tombstone.Parameters, "tx", tx);
            _c.TombstoneAttributes.ApplyTo(tombstone);
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
        var statement = rows == RowsPerInsert
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
        AddTransactionId(parameters, "tx", tx);
        statement.ApplyTo(command);
        await command.ExecuteNonQueryAsync(ct);
    }

    // Registers the write as in flight from its id up, on a connection of its own in autocommit, so
    // readers see it at once and stop below it until the write commits. The newest committed id is
    // read after the registration committed; a write whose id isn't above it takes one that is, since
    // a reader that resolved its token before the registration became visible may have settled there.
    private async Task<(string Id, Slot Slot)> Register(CancellationToken ct)
    {
        var lowerBound = MonotonicUlid.Next(_timeProvider);
        long slot;
        string? newest;
        // The shared data source allows the user variable the claim parks the slot in.
        await using (var side = await _dataSource.OpenConnectionAsync(ct))
        {
            await using var register = side.CreateCommand();
            AddTransactionId(register.Parameters, "lower_bound", lowerBound.ToString());
            _c.Register.ApplyTo(register);
            await using var reader = await register.ExecuteReaderAsync(ct);
            do
            {
                if (reader.FieldCount == 2 && await reader.ReadAsync(ct)) break;
            } while (await reader.NextResultAsync(ct));
            slot = Convert.ToInt64(reader.GetValue(0));
            newest = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var id = newest is not null && string.CompareOrdinal(newest, lowerBound.ToString()) >= 0
            ? MonotonicUlid.NextAfter(_timeProvider, Ulid.Parse(newest))
            : lowerBound;
        return (id.ToString(), new Slot(slot, lowerBound.ToString()));
    }

    private readonly record struct Slot(long Number, string LowerBound);

    private readonly MySqlDataSource _dataSource;

    private async Task InsertTransaction(MySqlConnection connection, MySqlTransaction transaction, string tx,
        Slot slot, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        AddTransactionId(command.Parameters, "id", tx);
        AddTransactionId(command.Parameters, "lower_bound", slot.LowerBound);
        command.Parameters.Add(new MySqlParameter("slot", MySqlDbType.Int64) { Value = slot.Number });
        command.Parameters.Add(new MySqlParameter("created_at", MySqlDbType.DateTime) { Value = createdAt.UtcDateTime });
        _c.InsertTransaction.ApplyTo(command);
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
        var now = _timeProvider.GetUtcNow();
        var (tx, slot) = await Register(ct);
        // First, so the slot is locked for the rest of the write and the sweeper leaves it alone.
        await InsertTransaction(mySqlConnection, mySqlTransaction, tx, slot, now, ct);

        foreach (var group in filter.Relations.Where(FitsColumns).GroupBy(static f => RelationFilterShape.Of(f)))
        {
            var filtersJson = JsonSerializer.Serialize(group.ToArray(), DeleteFilterJsonContext.Default.DeleteRelationsFilterArray);
            await ExecuteDelete(mySqlConnection, mySqlTransaction, _c.DeleteRelations[group.Key], tx, filtersJson, ct);
        }

        foreach (var group in filter.Attributes.Where(FitsColumns).GroupBy(static f => f.Attribute is not null))
        {
            var filtersJson = JsonSerializer.Serialize(group.ToArray(), DeleteFilterJsonContext.Default.DeleteAttributesFilterArray);
            var statement = group.Key ? _c.DeleteAttributesByName : _c.DeleteAttributesOfEntity;
            await ExecuteDelete(mySqlConnection, mySqlTransaction, statement, tx, filtersJson, ct);
        }

        return new SnapToken(tx);
    }

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
    public async Task NotifyCommitted(SnapToken snapToken)
    {
        await WaitUntilSettled(snapToken);
        await _options.NotifyDataWritten(_provider, snapToken);
    }

    // A committed write is visible to requests without a token only once the latest settled token
    // reaches it, which needs every older write still open to end. Each round waits on the slot locks
    // of the older writes registered so far, then reads the latest settled token; a write registered
    // later with an older id (a node whose clock is behind), or one between registering and locking
    // its slot, is caught by the next round. Past the timeout the write returns anyway, and requests
    // without a token see it once it settles.
    private async Task WaitUntilSettled(SnapToken snapToken)
    {
        var deadline = _timeProvider.GetTimestamp() + (long)(_options.SettleTimeout.TotalSeconds * _timeProvider.TimestampFrequency);
        var pause = TimeSpan.FromMilliseconds(1);
        while (true)
        {
            var remaining = deadline - _timeProvider.GetTimestamp();
            if (remaining <= 0) return;
            // The lock wait timeout is in whole seconds.
            var remainingSeconds = (int)Math.Clamp(Math.Ceiling((double)remaining / _timeProvider.TimestampFrequency), 1, 1073741824);

            string? settled = null;
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = _c.WaitUntilSettled;
                command.CommandTimeout = 0;
                command.Parameters.AddWithValue("@timeout_s", remainingSeconds);
                command.Parameters.AddWithValue("@snap_token", snapToken.Value);
                await using var reader = await command.ExecuteReaderAsync();
                do
                {
                    if (reader.GetName(0) == "id" && await reader.ReadAsync() && !reader.IsDBNull(0))
                        settled = reader.GetString(0);
                } while (await reader.NextResultAsync());
            }
            catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.LockWaitTimeout)
            {
                return;
            }

            if (settled is not null && string.CompareOrdinal(settled, snapToken.Value) >= 0) return;
            await Task.Delay(pause, _timeProvider);
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, TimeSpan.FromMilliseconds(20).Ticks));
        }
    }

    private static async Task ExecuteDelete(MySqlConnection connection, MySqlTransaction transaction,
        MySqlStatement statement, string tx, string filtersJson, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        AddTransactionId(command.Parameters, "tx", tx);
        // The relation delete for a filter with no field set matches every live tuple and reads no
        // filters.
        if (statement.Reads("filters"))
            command.Parameters.Add(new MySqlParameter("filters", MySqlDbType.JSON) { Value = filtersJson });
        statement.ApplyTo(command);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddValue(MySqlParameterCollection parameters, string name, string value) =>
        parameters.Add(new MySqlParameter(name, MySqlDbType.VarChar) { Value = value });

    private static void AddTransactionId(MySqlParameterCollection parameters, string name, string tx) =>
        parameters.Add(new MySqlParameter(name, MySqlDbType.String) { Size = 26, Value = tx });
}

internal readonly record struct MySqlAttributeKey(string EntityType, string Attribute, string EntityId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MySqlAttributeKey[]))]
internal partial class MySqlWriterJsonContext : JsonSerializerContext;
