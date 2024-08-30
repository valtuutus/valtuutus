using Valtuutus.Core;
using Valtuutus.Core.Data;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;


[Collection("SqlServerSpec")]
public sealed class DataEngineSpecs : BaseDataEngineSpecs
{

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddSqlServer(_ =>  ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
    
    public DataEngineSpecs(SqlServerFixture fixture) : base(fixture){}
    
    [Fact]
    public async Task WritingData_ShouldAssociateRelationWithTransactionId()
    {
        
        // act
        var provider = Provider.GetRequiredService<IDataWriterProvider>();
        var snapToken = await provider.Write([new RelationTuple("project", "1", "member", "user", "1")], [], default);
        var transactionId = Ulid.Parse(snapToken.Value);

        // assert
        using var db = ((IWithDbConnectionFactory)Fixture).DbFactory();
        var relationCount = await db.ExecuteScalarAsync<bool>("SELECT (SELECT COUNT(*) FROM relation_tuples WHERE created_tx_id = @id)", 
            new { id = transactionId });
        var exists = await db.ExecuteScalarAsync<bool>("""
                                                          SELECT
                                                          CASE
                                                              WHEN EXISTS(SELECT 1 FROM transactions WHERE id = @id)
                                                                   THEN 1
                                                              ELSE 0
                                                          END
                                                       """, 
            new { id = transactionId });
        
        relationCount.Should().BeTrue();
        
        exists.Should().BeTrue();
    }
    
    [Fact]
    public async Task DeletingData_ShouldReturnTransactionId()
    {
        // arrange
        var provider = Provider.GetRequiredService<IDataWriterProvider>();
        
        // act
        var newSnapToken = await provider.Delete(new DeleteFilter
        {
            Relations = new[] { new DeleteRelationsFilter
            {
                EntityType = "project",
                EntityId = "1",
                Relation = "member",
                SubjectType = "user",
                SubjectId = "1"
            
            } }
        }, default);
        
        
        // assert
        using var db = ((IWithDbConnectionFactory)Fixture).DbFactory();

        var newTransactionId = Ulid.Parse(newSnapToken.Value);
        // new transaction should exist
        var newTransaction = await db.ExecuteScalarAsync<bool>("""
                                                               SELECT
                                                               CASE
                                                                   WHEN EXISTS(SELECT 1 FROM transactions WHERE id = @id)
                                                                        THEN 1
                                                                   ELSE 0
                                                               END 
                                                               """,
            new { id = newTransactionId });
        
        newTransaction.Should().BeTrue();
    }
    
    protected override async Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples()
    {
        using var db = ((IWithDbConnectionFactory)Fixture).DbFactory();
        var relations = (await db.QueryAsync<RelationTuple>("""
            SELECT  entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation from relation_tuples where deleted_tx_id is null
            """)).ToArray();
        var attributes =
            (await db.QueryAsync<AttributeTuple>("select entity_type, entity_id, attribute,value from attributes where deleted_tx_id is null")).ToArray();
        
        return (relations, attributes);

    }
}