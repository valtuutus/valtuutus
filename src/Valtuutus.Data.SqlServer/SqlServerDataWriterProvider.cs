using System.Collections.Concurrent;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.SqlServer.Utils;
using Dapper;
using FastMember;
using Microsoft.Data.SqlClient;
using Valtuutus.Data.Db;
using System.Data;

namespace Valtuutus.Data.SqlServer;

internal sealed class SqlServerDataWriterProvider : IDbDataWriterProvider
{
    private readonly DbConnectionFactory _factory;
    private readonly ValtuutusDataOptions _options;
    private readonly IServiceProvider _provider;

    private static readonly ConcurrentDictionary<DbQueryCacheKey, WriterCommands> CommandCache = new();
    private readonly WriterCommands _c;

    public SqlServerDataWriterProvider(DbConnectionFactory factory,
        ValtuutusDataOptions options,
        IServiceProvider provider,
        IValtuutusDbOptions dbOptions)
    {
        _factory = factory;
        _options = options;
        _provider = provider;
        _c = CommandCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildCommands(key));
    }

    private static WriterCommands BuildCommands(DbQueryCacheKey key)
    {
        return new WriterCommands
        {
            InsertTransaction =
                $"INSERT INTO [{key.Schema}].[{key.TransactionsTable}] (id, created_at) VALUES (@id, @created_at)",
            RelationsDestinationTableName = $"[{key.Schema}].[{key.RelationsTable}]",
            MergeAttributes = $"""
                               MERGE INTO [{key.Schema}].[{key.AttributesTable}] AS target
                               USING #temp_attributes AS source
                               ON (target.entity_type = source.entity_type
                                   AND target.entity_id = source.entity_id
                                   AND target.attribute = source.attribute)
                               WHEN MATCHED AND target.deleted_tx_id IS NULL THEN
                                   UPDATE SET target.deleted_tx_id = source.created_tx_id;

                               INSERT INTO [{key.Schema}].[{key.AttributesTable}] (entity_type, entity_id, attribute, value, created_tx_id)
                               SELECT source.entity_type, source.entity_id, source.attribute, source.value, source.created_tx_id
                               FROM #temp_attributes AS source;
                               """,
            DeleteRelations =
                $"UPDATE [{key.Schema}].[{key.RelationsTable}] set deleted_tx_id = @SnapToken /**where**/",
            DeleteAttributes =
                $"UPDATE [{key.Schema}].[{key.AttributesTable}] set deleted_tx_id = @SnapToken /**where**/",
        };
    }

    private sealed record WriterCommands
    {
        public required string InsertTransaction { get; init; }
        public required string RelationsDestinationTableName { get; init; }
        public required string MergeAttributes { get; init; }
        public required string DeleteRelations { get; init; }
        public required string DeleteAttributes { get; init; }
    }

    public async Task<SnapToken> Write(
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        await using var db = (SqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Write(db, relations, attributes, ct);
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
        var transaction = (SqlTransaction)await ((SqlConnection)connection).BeginTransactionAsync(ct);
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
        var transactionId = Ulid.NewUlid();
        await InsertTransaction((SqlConnection)connection, transactionId, (SqlTransaction)transaction, ct);

        using var relationsBulkCopy = new SqlBulkCopy((SqlConnection)connection, SqlBulkCopyOptions.Default, (SqlTransaction)transaction);
        relationsBulkCopy.DestinationTableName = _c.RelationsDestinationTableName;

        await
        using var relationsReader = ObjectReader.Create(relations.Select(x => new
        {
            x.EntityType,
            x.EntityId,
            x.SubjectType,
            x.SubjectId,
            x.Relation,
            x.SubjectRelation,
            TransactionId = transactionId.ToString()
        }));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Relation", "relation"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectType", "subject_type"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectId", "subject_id"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectRelation", "subject_relation"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));

        await relationsBulkCopy.WriteToServerAsync(relationsReader, ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "CREATE TABLE #temp_attributes (entity_type NVARCHAR(256), entity_id NVARCHAR(64), attribute NVARCHAR(64), value NVARCHAR(256), created_tx_id CHAR(26))",
            transaction: transaction, cancellationToken: ct));

        using var attributesBulkCopy = new SqlBulkCopy((SqlConnection)connection, SqlBulkCopyOptions.Default, (SqlTransaction)transaction);
        attributesBulkCopy.DestinationTableName = "#temp_attributes";

        await

        using var attributesReader = ObjectReader.Create(attributes.Select(t => new
        {
            t.EntityType,
            t.EntityId,
            t.Attribute,
            Value = t.Value.ToJsonString(),
            TransactionId = transactionId.ToString()
        }));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Attribute", "attribute"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Value", "value"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));


        await attributesBulkCopy.WriteToServerAsync(attributesReader, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            _c.MergeAttributes, transaction: transaction, cancellationToken: ct));

        var snapToken = new SnapToken(transactionId.ToString());
        await(_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        await using var db = (SqlConnection)_factory();
        await db.OpenAsync(ct);

        return await Delete(db, filter, ct);
    }

    public async Task<SnapToken> Delete(IDbConnection connection, DeleteFilter filter, CancellationToken ct)
    {
        var transaction = (SqlTransaction)await ((SqlConnection)connection).BeginTransactionAsync(ct);
        var snapToken = await Delete(connection, transaction, filter, ct);

        await transaction.CommitAsync(ct);
        return snapToken;
    }

    public async Task<SnapToken> Delete(IDbConnection connection, IDbTransaction transaction, DeleteFilter filter, CancellationToken ct)
    {
        var transactId = Ulid.NewUlid();
        await InsertTransaction((SqlConnection)connection, transactId, (SqlTransaction)transaction, ct);

        var snapTokenParam = new
        {
            SnapToken = new DbString { Length = 26, Value = transactId.ToString(), IsFixedLength = true }
        };

        if (filter.Relations.Length > 0)
        {
            var relationsBuilder = new SqlBuilder();
            relationsBuilder = relationsBuilder.FilterDeleteRelations(filter.Relations);
            var queryTemplate =
                relationsBuilder.AddTemplate(_c.DeleteRelations,
                    snapTokenParam);

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

        var snapToken = new SnapToken(transactId.ToString());
        await(_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    private async Task InsertTransaction(SqlConnection db, Ulid transactId, SqlTransaction transaction,
        CancellationToken ct)
    {
        await db.ExecuteAsync(new CommandDefinition(
            _c.InsertTransaction,
            new { id = transactId, created_at = DateTimeOffset.UtcNow }, transaction: transaction,
            cancellationToken: ct));
    }
}