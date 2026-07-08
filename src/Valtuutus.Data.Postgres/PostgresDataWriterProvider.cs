using Valtuutus.Core;
using Valtuutus.Core.Data;
using Npgsql;
using NpgsqlTypes;
using Valtuutus.Data.Db;
using System.Data;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Valtuutus.Data.Postgres;

public class PostgresDataWriterProvider : IDbDataWriterProvider
{
    private readonly DbConnectionFactory _factory;
    private readonly IServiceProvider _provider;
    private readonly ValtuutusDataOptions _options;

    private sealed record WriterCommands
    {
        public required string CopyRelations { get; init; }
        public required string InsertTransaction { get; init; }
        public required string DeleteRelations { get; init; }
        public required string DeleteAttributes { get; init; }
        public required string MergeAttributes { get; init; }
    }

    private static readonly ConcurrentDictionary<DbQueryCacheKey, WriterCommands> CommandCache = new();
    private readonly WriterCommands _c;

    public PostgresDataWriterProvider(DbConnectionFactory factory,
        IServiceProvider provider,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions)
    {
        _factory = factory;
        _provider = provider;
        _options = options;
        _c = CommandCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildCommands(key));
    }

    private static WriterCommands BuildCommands(DbQueryCacheKey key) => new()
    {
        CopyRelations =
            $"copy {key.Schema}.{key.RelationsTable} (entity_type, entity_id, relation, subject_type, subject_id, subject_relation, created_tx_id) from STDIN (FORMAT BINARY)",
        InsertTransaction =
            $"INSERT INTO {key.Schema}.{key.TransactionsTable} (id, created_at) VALUES (@id, @created_at)",
        DeleteRelations = $"""
            UPDATE {key.Schema}.{key.RelationsTable} r
            SET deleted_tx_id = @SnapToken
            FROM jsonb_to_recordset(@Filters::jsonb) AS f(
                entity_type text, entity_id text, subject_type text,
                subject_id text, relation text, subject_relation text)
            WHERE r.deleted_tx_id IS NULL
              AND (f.entity_type IS NULL OR r.entity_type = f.entity_type)
              AND (f.entity_id IS NULL OR r.entity_id = f.entity_id)
              AND (f.subject_type IS NULL OR r.subject_type = f.subject_type)
              AND (f.subject_id IS NULL OR r.subject_id = f.subject_id)
              AND (f.relation IS NULL OR r.relation = f.relation)
              AND (f.subject_relation IS NULL OR r.subject_relation = f.subject_relation)
            """,
        DeleteAttributes = $"""
            UPDATE {key.Schema}.{key.AttributesTable} a
            SET deleted_tx_id = @SnapToken
            FROM jsonb_to_recordset(@Filters::jsonb) AS f(entity_type text, entity_id text, attribute text)
            WHERE a.deleted_tx_id IS NULL
              AND a.entity_type = f.entity_type
              AND a.entity_id = f.entity_id
              AND (f.attribute IS NULL OR a.attribute = f.attribute)
            """,
        MergeAttributes = $""""
                           MERGE INTO {key.Schema}.{key.AttributesTable} AS target
                           USING temp_attributes AS source
                           ON (target.entity_type = source.entity_type
                               AND target.entity_id = source.entity_id
                               AND target.attribute = source.attribute)
                           WHEN MATCHED AND target.deleted_tx_id IS NULL THEN
                               UPDATE SET deleted_tx_id = source.created_tx_id;

                           INSERT INTO {key.Schema}.{key.AttributesTable} (entity_type, entity_id, attribute, value, created_tx_id)
                           SELECT entity_type, entity_id, attribute, value, created_tx_id
                           FROM temp_attributes;

                           DROP TABLE temp_attributes;
                           """"
    };

    public async Task<SnapToken> Write(
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        await using var db = (NpgsqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Write(db, relations, attributes, ct);
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
#if !NETCOREAPP3_0_OR_GREATER
        await using var transaction = ((NpgsqlConnection)connection).BeginTransaction();
#else
        await using var transaction = await ((NpgsqlConnection)connection).BeginTransactionAsync(ct);
#endif
        var snapToken = await Write(connection, transaction, relations, attributes, ct);
        await transaction.CommitAsync(ct);

        return snapToken;
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IDbTransaction transaction,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        var transactId = Ulid.NewUlid();

        await InsertTransaction((NpgsqlConnection)connection, transactId, (NpgsqlTransaction)transaction, ct);

        await WriteRelationsAsync((NpgsqlConnection)connection, (NpgsqlTransaction)transaction, transactId, relations, ct);
        await WriteAttributesAsync((NpgsqlConnection)connection, (NpgsqlTransaction)transaction, transactId, attributes, ct);

        var snapToken = new SnapToken(transactId.ToString());
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    protected virtual async Task WriteRelationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Ulid transactId, IEnumerable<RelationTuple> relations, CancellationToken ct)
    {
        await using var relationsWriter = await connection.BeginBinaryImportAsync(_c.CopyRelations, ct);
        foreach (var record in relations)
        {
            await relationsWriter.StartRowAsync(ct);
            await relationsWriter.WriteAsync(record.EntityType, ct);
            await relationsWriter.WriteAsync(record.EntityId, ct);
            await relationsWriter.WriteAsync(record.Relation, ct);
            await relationsWriter.WriteAsync(record.SubjectType, ct);
            await relationsWriter.WriteAsync(record.SubjectId, ct);
            await relationsWriter.WriteAsync(record.SubjectRelation, ct);
            await relationsWriter.WriteAsync(transactId.ToString(), NpgsqlDbType.Char, ct);
        }
        await relationsWriter.CompleteAsync(ct);
        await relationsWriter.CloseAsync(ct);
    }

    protected virtual async Task WriteAttributesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Ulid transactId, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        await using (var createTempTableCommand = connection.CreateCommand())
        {
            createTempTableCommand.Transaction = transaction;
            createTempTableCommand.CommandText =
                "CREATE TEMPORARY TABLE temp_attributes (entity_type VARCHAR(256), entity_id VARCHAR(64), attribute VARCHAR(64), value JSONB, created_tx_id CHAR(26))";
            await createTempTableCommand.ExecuteNonQueryAsync(ct);
        }

        await using var attributesWriter = await connection.BeginBinaryImportAsync(
            "copy temp_attributes (entity_type, entity_id, attribute, value, created_tx_id) from STDIN (FORMAT BINARY)",
            ct);

        foreach (var record in attributes)
        {
            await attributesWriter.StartRowAsync(ct);
            await attributesWriter.WriteAsync(record.EntityType, ct);
            await attributesWriter.WriteAsync(record.EntityId, ct);
            await attributesWriter.WriteAsync(record.Attribute, ct);
            await attributesWriter.WriteAsync(record.Value.ToJsonString(), NpgsqlDbType.Jsonb, ct);
            await attributesWriter.WriteAsync(transactId.ToString(), NpgsqlDbType.Char, ct);
        }

        await attributesWriter.CompleteAsync(ct);
        await attributesWriter.CloseAsync(ct);

        await using var mergeCommand = connection.CreateCommand();
        mergeCommand.Transaction = transaction;
        mergeCommand.CommandText = _c.MergeAttributes;
        await mergeCommand.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertTransaction(NpgsqlConnection db, Ulid transactId,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _c.InsertTransaction;
        command.Parameters.Add(new NpgsqlParameter<string>("id", NpgsqlDbType.Char) { Size = 26, TypedValue = transactId.ToString() });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", NpgsqlDbType.TimestampTz) { TypedValue = DateTimeOffset.UtcNow });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SnapToken> Delete(
        DeleteFilter filter,
        CancellationToken ct
    ) {
        await using var db = (NpgsqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Delete(db, filter, ct);
    }

    public async Task<SnapToken> Delete(
        IDbConnection connection,
        DeleteFilter filter,
        CancellationToken ct
    ) {
#if !NETCOREAPP3_0_OR_GREATER
        await using var transaction = ((NpgsqlConnection)connection).BeginTransaction();
#else
        await using var transaction = await ((NpgsqlConnection)connection).BeginTransactionAsync(ct);
#endif
        var snapToken = await Delete(connection, transaction, filter, ct);
        await transaction.CommitAsync(ct);

        return snapToken;
    }

    public async Task<SnapToken> Delete(
        IDbConnection connection,
        IDbTransaction transaction,
        DeleteFilter filter,
        CancellationToken ct
    ) {
        var transactId = Ulid.NewUlid();
        var npgsqlConnection = (NpgsqlConnection)connection;
        var npgsqlTransaction = (NpgsqlTransaction)transaction;
        var snapTokenValue = transactId.ToString();

        if (filter.Relations.Length > 0)
        {
            var filtersJson = JsonSerializer.Serialize(filter.Relations, DeleteFilterJsonContext.Default.DeleteRelationsFilterArray);
            await ExecuteDeleteBatch(npgsqlConnection, npgsqlTransaction, _c.DeleteRelations, snapTokenValue, filtersJson, ct);
        }

        if (filter.Attributes.Length > 0)
        {
            var filtersJson = JsonSerializer.Serialize(filter.Attributes, DeleteFilterJsonContext.Default.DeleteAttributesFilterArray);
            await ExecuteDeleteBatch(npgsqlConnection, npgsqlTransaction, _c.DeleteAttributes, snapTokenValue, filtersJson, ct);
        }

        await InsertTransaction(npgsqlConnection, transactId, npgsqlTransaction, ct);
        var snapToken = new SnapToken(snapTokenValue);
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    private static async Task ExecuteDeleteBatch(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, string snapToken, string filtersJson, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter<string>("SnapToken", NpgsqlDbType.Char) { Size = 26, TypedValue = snapToken });
        command.Parameters.Add(new NpgsqlParameter<string>("Filters", NpgsqlDbType.Text) { TypedValue = filtersJson });
        await command.ExecuteNonQueryAsync(ct);
    }
}
