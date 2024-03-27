using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Configuration;
using Valtuutus.Data.SqlServer.Utils;
using Dapper;
using FastMember;
using IdGen;
using Microsoft.Data.SqlClient;
using Sqids;

namespace Valtuutus.Data.SqlServer;

public sealed class SqlServerDataWriterProvider : IDataWriterProvider
{
    private readonly DbConnectionFactory _factory;
    private readonly IIdGenerator<long> _idGenerator;
    private readonly SqidsEncoder<long> _encoder;

    public SqlServerDataWriterProvider(DbConnectionFactory factory, IIdGenerator<long> idGenerator, SqidsEncoder<long> encoder)
    {
        _factory = factory;
        _idGenerator = idGenerator;
        _encoder = encoder;
    }
    
    public async Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        var transactionId = _idGenerator.CreateId();
        
        await using var db = (SqlConnection) _factory();
        await db.OpenAsync(ct);
        var transaction = db.BeginTransaction();
        
        await InsertTransaction(db, transactionId, transaction, ct);
        
        var relationsBulkCopy = new SqlBulkCopy(db, SqlBulkCopyOptions.Default, transaction);
        relationsBulkCopy.DestinationTableName = "relation_tuples";

        await using var relationsReader = ObjectReader.Create(relations.Select(x => new
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

        await using var attributesReader = ObjectReader.Create(attributes.Select( t => new { t.EntityType, t.EntityId, t.Attribute, Value = t.Value.ToJsonString(),
            TransactionId = transactionId }));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityType", "entity_type"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("EntityId", "entity_id"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Attribute", "attribute"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("Value", "value"));
        attributesBulkCopy.ColumnMappings.Add(new SqlBulkCopyColumnMapping("TransactionId", "created_tx_id"));


        await attributesBulkCopy.WriteToServerAsync(attributesReader, ct);

        await transaction.CommitAsync(ct);
        
        return new SnapToken(_encoder.Encode(transactionId));
    }
    

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        var transactId = _idGenerator.CreateId();
        
        await using var db = (SqlConnection) _factory();
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
        
        await transaction.CommitAsync(ct);
        return new SnapToken(_encoder.Encode(transactId));
    }
    
    private static async Task InsertTransaction(SqlConnection db, long transactId, SqlTransaction transaction, CancellationToken ct)
    {
        await db.ExecuteAsync( new CommandDefinition("INSERT INTO transactions (id, created_at) VALUES (@id, @created_at)", new
        {
            id = transactId,
            created_at = DateTimeOffset.UtcNow
        }, transaction: transaction, cancellationToken: ct));
    }
    
}