using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.SqlServer.Utils;
using Dapper;
using FastMember;
using Microsoft.Data.SqlClient;

namespace Valtuutus.Data.SqlServer;

public sealed class SqlServerDataWriterProvider : IDataWriterProvider
{
    private readonly DbConnectionFactory _factory;

    public SqlServerDataWriterProvider(DbConnectionFactory factory)
    {
        _factory = factory;
    }
    
    public async Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        var transactionId = Ulid.NewUlid();
        
#if NETSTANDARD2_0
        using var db = (SqlConnection) _factory();
#else
        await using var db = (SqlConnection) _factory();
#endif
        await db.OpenAsync(ct);
        var transaction = db.BeginTransaction();
        
        await InsertTransaction(db, transactionId, transaction, ct);
        
        var relationsBulkCopy = new SqlBulkCopy(db, SqlBulkCopyOptions.Default, transaction);
        relationsBulkCopy.DestinationTableName = "relation_tuples";

#if NETSTANDARD2_0
#else
        await 
#endif
        using var relationsReader = ObjectReader.Create(relations.Select(x => new
        {
            x.EntityType, x.EntityId, x.SubjectType, x.SubjectId, x.Relation, x.SubjectRelation, TransactionId = transactionId
        }));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Relation", "relation"));
        relationsBulkCopy.ColumnMappings.Add( new SqlBulkCopyColumnMapping("SubjectType", "subject_type"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectId", "subject_id"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("SubjectRelation", "subject_relation"));
        relationsBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));
        
        await relationsBulkCopy.WriteToServerAsync(relationsReader, ct);
        
        using var attributesBulkCopy = new SqlBulkCopy(db, SqlBulkCopyOptions.Default, transaction);
        attributesBulkCopy.DestinationTableName = "attributes";

#if NETSTANDARD2_0
#else
        await 
#endif
            
            using var attributesReader = ObjectReader.Create(attributes.Select( t => new { t.EntityType, t.EntityId, t.Attribute, Value = t.Value.ToJsonString(),
            TransactionId = transactionId }));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Attribute", "attribute"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Value", "value"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));


        await attributesBulkCopy.WriteToServerAsync(attributesReader, ct);

#if NETSTANDARD2_0
        transaction.Commit();
#else
        await transaction.CommitAsync(ct);
#endif
        
        return new SnapToken(transactionId.ToString());
    }
    

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        var transactId = Ulid.NewUlid();

#if NETSTANDARD2_0
        using var db = (SqlConnection) _factory();
#else
        await using var db = (SqlConnection) _factory();
#endif
        await db.OpenAsync(ct);
        var transaction = db.BeginTransaction();
        await InsertTransaction(db, transactId, transaction, ct);
        
        if (filter.Relations.Length > 0)
        {
            var relationsBuilder = new SqlBuilder();
            relationsBuilder = relationsBuilder.FilterDeleteRelations(filter.Relations);
            var queryTemplate = relationsBuilder.AddTemplate(@"DELETE FROM relation_tuples /**where**/");

            await db.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction:transaction));
            
        }

        if (filter.Attributes.Length > 0)
        {
            var attributesBuilder = new SqlBuilder();
            attributesBuilder = attributesBuilder.FilterDeleteAttributes(filter.Attributes);
            var queryTemplate = attributesBuilder.AddTemplate(@"DELETE FROM attributes /**where**/");

            await db.ExecuteAsync(new CommandDefinition(queryTemplate.RawSql, queryTemplate.Parameters,
                cancellationToken: ct, transaction: transaction));
            
        }

#if NETSTANDARD2_0
        transaction.Commit();
#else
        await transaction.CommitAsync(ct);
#endif
        return new SnapToken(transactId.ToString());
    }
    
    private static async Task InsertTransaction(SqlConnection db, Ulid transactId, SqlTransaction transaction, CancellationToken ct)
    {
        await db.ExecuteAsync( new CommandDefinition("INSERT INTO transactions (id, created_at) VALUES (@id, @created_at)", new
        {
            id = transactId,
            created_at = DateTimeOffset.UtcNow
        }, transaction: transaction, cancellationToken: ct));
    }
    
}