using System.Collections.Concurrent;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using FastMember;
using Microsoft.Data.SqlClient;
using Valtuutus.Data.Db;
using System.Data;
using System.Text.Json;

namespace Valtuutus.Data.SqlServer;

public class SqlServerDataWriterProvider : IDbDataWriterProvider
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

                               DROP TABLE #temp_attributes;
                               """,
            DeleteRelations = $"""
                UPDATE r
                SET deleted_tx_id = @SnapToken
                FROM [{key.Schema}].[{key.RelationsTable}] r
                CROSS APPLY OPENJSON(@Filters) WITH (
                    entity_type NVARCHAR(256) '$.entity_type',
                    entity_id NVARCHAR(64) '$.entity_id',
                    subject_type NVARCHAR(256) '$.subject_type',
                    subject_id NVARCHAR(64) '$.subject_id',
                    relation NVARCHAR(64) '$.relation',
                    subject_relation NVARCHAR(64) '$.subject_relation'
                ) AS f
                WHERE r.deleted_tx_id IS NULL
                  AND (f.entity_type IS NULL OR r.entity_type = f.entity_type)
                  AND (f.entity_id IS NULL OR r.entity_id = f.entity_id)
                  AND (f.subject_type IS NULL OR r.subject_type = f.subject_type)
                  AND (f.subject_id IS NULL OR r.subject_id = f.subject_id)
                  AND (f.relation IS NULL OR r.relation = f.relation)
                  AND (f.subject_relation IS NULL OR r.subject_relation = f.subject_relation)
                """,
            DeleteAttributes = $"""
                UPDATE a
                SET deleted_tx_id = @SnapToken
                FROM [{key.Schema}].[{key.AttributesTable}] a
                CROSS APPLY OPENJSON(@Filters) WITH (
                    entity_type NVARCHAR(256) '$.entity_type',
                    entity_id NVARCHAR(64) '$.entity_id',
                    attribute NVARCHAR(64) '$.attribute'
                ) AS f
                WHERE a.deleted_tx_id IS NULL
                  AND a.entity_type = f.entity_type
                  AND a.entity_id = f.entity_id
                  AND (f.attribute IS NULL OR a.attribute = f.attribute)
                """,
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

        await WriteRelationsAsync((SqlConnection)connection, (SqlTransaction)transaction, transactionId, relations, ct);
        await WriteAttributesAsync((SqlConnection)connection, (SqlTransaction)transaction, transactionId, attributes, ct);

        var snapToken = new SnapToken(transactionId.ToString());
        await(_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    protected virtual async Task WriteRelationsAsync(SqlConnection connection, SqlTransaction transaction, Ulid transactId, IEnumerable<RelationTuple> relations, CancellationToken ct)
    {
        using var relationsBulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
        relationsBulkCopy.DestinationTableName = _c.RelationsDestinationTableName;

        await using var relationsReader = ObjectReader.Create(relations.Select(x => new
        {
            x.EntityType,
            x.EntityId,
            x.SubjectType,
            x.SubjectId,
            x.Relation,
            x.SubjectRelation,
            TransactionId = transactId.ToString()
        }));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Relation", "relation"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectType", "subject_type"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectId", "subject_id"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectRelation", "subject_relation"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));

        await relationsBulkCopy.WriteToServerAsync(relationsReader, ct);
    }

    protected virtual async Task WriteAttributesAsync(SqlConnection connection, SqlTransaction transaction, Ulid transactId, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        await using (var createTempTableCommand = connection.CreateCommand())
        {
            createTempTableCommand.Transaction = transaction;
            createTempTableCommand.CommandText =
                "CREATE TABLE #temp_attributes (entity_type NVARCHAR(256), entity_id NVARCHAR(64), attribute NVARCHAR(64), value NVARCHAR(256), created_tx_id CHAR(26))";
            await createTempTableCommand.ExecuteNonQueryAsync(ct);
        }

        using var attributesBulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
        attributesBulkCopy.DestinationTableName = "#temp_attributes";

        await using var attributesReader = ObjectReader.Create(attributes.Select(t => new
        {
            t.EntityType,
            t.EntityId,
            t.Attribute,
            Value = t.Value.ToJsonString(),
            TransactionId = transactId.ToString()
        }));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Attribute", "attribute"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Value", "value"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));

        await attributesBulkCopy.WriteToServerAsync(attributesReader, ct);

        await using var mergeCommand = connection.CreateCommand();
        mergeCommand.Transaction = transaction;
        mergeCommand.CommandText = _c.MergeAttributes;
        await mergeCommand.ExecuteNonQueryAsync(ct);
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
        var sqlConnection = (SqlConnection)connection;
        var sqlTransaction = (SqlTransaction)transaction;
        await InsertTransaction(sqlConnection, transactId, sqlTransaction, ct);

        var snapTokenValue = transactId.ToString();

        if (filter.Relations.Length > 0)
        {
            var filtersJson = JsonSerializer.Serialize(filter.Relations, DeleteFilterJsonContext.Default.DeleteRelationsFilterArray);
            await ExecuteDeleteBatch(sqlConnection, sqlTransaction, _c.DeleteRelations, snapTokenValue, filtersJson, ct);
        }

        if (filter.Attributes.Length > 0)
        {
            var filtersJson = JsonSerializer.Serialize(filter.Attributes, DeleteFilterJsonContext.Default.DeleteAttributesFilterArray);
            await ExecuteDeleteBatch(sqlConnection, sqlTransaction, _c.DeleteAttributes, snapTokenValue, filtersJson, ct);
        }

        var snapToken = new SnapToken(snapTokenValue);
        await(_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    private static async Task ExecuteDeleteBatch(SqlConnection connection, SqlTransaction transaction,
        string sql, string snapToken, string filtersJson, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(new SqlParameter("@SnapToken", SqlDbType.NChar, 26) { Value = snapToken });
        command.Parameters.Add(new SqlParameter("@Filters", SqlDbType.NVarChar, -1) { Value = filtersJson });
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task InsertTransaction(SqlConnection db, Ulid transactId, SqlTransaction transaction,
        CancellationToken ct)
    {
        await using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _c.InsertTransaction;
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.NChar, 26) { Value = transactId.ToString() });
        command.Parameters.Add(new SqlParameter("@created_at", SqlDbType.DateTimeOffset) { Value = DateTimeOffset.UtcNow });
        await command.ExecuteNonQueryAsync(ct);
    }
}
