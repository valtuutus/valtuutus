using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Postgres.Utils;
using Dapper;
using Npgsql;
using NpgsqlTypes;
using Valtuutus.Data.Db;
using System.Data;
using System.Collections.Concurrent;

namespace Valtuutus.Data.Postgres;

internal sealed class PostgresDataWriterProvider : IDbDataWriterProvider
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
        DeleteRelations =
            $@"UPDATE {key.Schema}.{key.RelationsTable} set deleted_tx_id = @SnapToken /**where**/",
        DeleteAttributes =
            $@"UPDATE {key.Schema}.{key.AttributesTable} set deleted_tx_id = @SnapToken /**where**/",
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

        await using var relationsWriter = await ((NpgsqlConnection)connection).BeginBinaryImportAsync(
            _c.CopyRelations,
            ct);
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

        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TEMPORARY TABLE temp_attributes (entity_type VARCHAR(256), entity_id VARCHAR(64), attribute VARCHAR(64), value JSONB, created_tx_id CHAR(26))",
            transaction, cancellationToken: ct));

        await using var attributesWriter = await ((NpgsqlConnection)connection).BeginBinaryImportAsync(
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

        await connection.ExecuteAsync(new CommandDefinition(
            _c.MergeAttributes, transaction, cancellationToken: ct));

        var snapToken = new SnapToken(transactId.ToString());
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    private async Task InsertTransaction(NpgsqlConnection db, Ulid transactId,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition(
            _c.InsertTransaction,
            new { id = transactId, created_at = DateTimeOffset.UtcNow }, transaction: transaction,
            cancellationToken: ct));
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

        var snapTokenParam = new
        {
            SnapToken = new DbString { Length = 26, Value = transactId.ToString(), IsFixedLength = true }
        };
        if (filter.Relations.Length > 0)
        {
            var relationsBuilder = new SqlBuilder();
            relationsBuilder = relationsBuilder.FilterDeleteRelations(filter.Relations);
            var queryTemplate =
                relationsBuilder.AddTemplate(
                    _c.DeleteRelations, snapTokenParam);

            await connection.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction: transaction));
        }

        if (filter.Attributes.Length > 0)
        {
            var attributesBuilder = new SqlBuilder();
            attributesBuilder = attributesBuilder.FilterDeleteAttributes(filter.Attributes);
            var queryTemplate =
                attributesBuilder.AddTemplate(_c.DeleteAttributes,
                    snapTokenParam);

            await connection.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction: transaction));
        }

        await InsertTransaction((NpgsqlConnection)connection, transactId, (NpgsqlTransaction)transaction, ct);
        var snapToken = new SnapToken(transactId.ToString());
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }
}
