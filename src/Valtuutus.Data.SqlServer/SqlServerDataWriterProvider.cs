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
    private static string? _deleteRelationsCommandText;
    private static string? _deleteAttributesCommandText;
    private static string? _mergeAttributesCommandText;
    private static string? _relationsDestinationTableName;
    private static string? _insertTransactionCommandText;
    private static readonly object Lock = new();


    public SqlServerDataWriterProvider(DbConnectionFactory factory, 
        ValtuutusDataOptions options,
        IServiceProvider provider,
        IValtuutusDbOptions dbOptions)
    {
        _factory = factory;
        _options = options;
        _provider = provider;
        InitializeCommands(dbOptions);
    }

    private static void InitializeCommands(IValtuutusDbOptions dbOptions)
    {
        if (_insertTransactionCommandText == null || _relationsDestinationTableName == null ||
            _mergeAttributesCommandText == null || _deleteRelationsCommandText == null ||
            _deleteAttributesCommandText == null)
        {
            lock (Lock)
            {
                if (_insertTransactionCommandText == null)
                {
                    _insertTransactionCommandText ??=
                        $"INSERT INTO [{dbOptions.Schema}].[{dbOptions.TransactionsTableName}] (id, created_at) VALUES (@id, @created_at)";
                    _relationsDestinationTableName ??= $"[{dbOptions.Schema}].[{dbOptions.RelationsTableName}]";
                    _mergeAttributesCommandText ??= $"""
                                                     MERGE INTO [{dbOptions.Schema}].[{dbOptions.AttributesTableName}] AS target
                                                     USING #temp_attributes AS source
                                                     ON (target.entity_type = source.entity_type 
                                                         AND target.entity_id = source.entity_id 
                                                         AND target.attribute = source.attribute)
                                                     WHEN MATCHED AND target.deleted_tx_id IS NULL THEN
                                                         UPDATE SET target.deleted_tx_id = source.created_tx_id;

                                                     INSERT INTO [{dbOptions.Schema}].[{dbOptions.AttributesTableName}] (entity_type, entity_id, attribute, value, created_tx_id)
                                                     SELECT source.entity_type, source.entity_id, source.attribute, source.value, source.created_tx_id
                                                     FROM #temp_attributes AS source;
                                                     """;

                    _deleteRelationsCommandText ??=
                        $"UPDATE [{dbOptions.Schema}].[{dbOptions.RelationsTableName}] set deleted_tx_id = @SnapToken /**where**/";
                    _deleteAttributesCommandText ??=
                        $"UPDATE [{dbOptions.Schema}].[{dbOptions.AttributesTableName}] set deleted_tx_id = @SnapToken /**where**/";
                }
            }
        }
    }

    public async Task<SnapToken> Write(
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
#if NETSTANDARD2_0
        using var db = (SqlConnection) _factory();
#else
        await using var db = (SqlConnection)_factory();
#endif
        await db.OpenAsync(ct);

        return await Write(db, relations, attributes, ct);
    }

    public async Task<SnapToken> Write(
        IDbConnection connection,
        IEnumerable<RelationTuple> relations,
        IEnumerable<AttributeTuple> attributes,
        CancellationToken ct
    ) {
#if NETSTANDARD2_0
        var transaction = connection.BeginTransaction();
#else
        var transaction = (SqlTransaction)await ((SqlConnection)connection).BeginTransactionAsync(ct);
#endif
        var snapToken = await Write(connection, transaction, relations, attributes, ct);

#if !NETCOREAPP3_0_OR_GREATER
        transaction.Commit();
#else
        await ((SqlTransaction)transaction).CommitAsync(ct);
#endif
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

        var relationsBulkCopy = new SqlBulkCopy((SqlConnection)connection, SqlBulkCopyOptions.Default, (SqlTransaction)transaction);
        relationsBulkCopy.DestinationTableName = _relationsDestinationTableName;

#if !NETSTANDARD2_0
        await
#endif
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

#if !NETSTANDARD2_0
        await
#endif

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
            _mergeAttributesCommandText!, transaction: transaction, cancellationToken: ct));

        var snapToken = new SnapToken(transactionId.ToString());
        await(_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
#if NETSTANDARD2_0
        using var db = (SqlConnection) _factory();
#else
        await using var db = (SqlConnection)_factory();
#endif
        await db.OpenAsync(ct);

        return await Delete(db, filter, ct);
    }

    public async Task<SnapToken> Delete(IDbConnection connection, DeleteFilter filter, CancellationToken ct)
    {
#if NETSTANDARD2_0
        var transaction = connection.BeginTransaction();
#else
        var transaction = (SqlTransaction)await ((SqlConnection)connection).BeginTransactionAsync(ct);
#endif
        var snapToken = await Delete(connection, transaction, filter, ct);

#if NETSTANDARD2_0
        transaction.Commit();
#else
        await ((SqlTransaction)transaction).CommitAsync(ct);
#endif
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
                relationsBuilder.AddTemplate(_deleteRelationsCommandText,
                    snapTokenParam);

            await connection.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction: transaction));
        }

        if (filter.Attributes.Length > 0)
        {
            var attributesBuilder = new SqlBuilder();
            attributesBuilder = attributesBuilder.FilterDeleteAttributes(filter.Attributes);
            var queryTemplate =
                attributesBuilder.AddTemplate(_deleteAttributesCommandText,
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
            _insertTransactionCommandText!,
            new { id = transactId, created_at = DateTimeOffset.UtcNow }, transaction: transaction,
            cancellationToken: ct));
    }
}