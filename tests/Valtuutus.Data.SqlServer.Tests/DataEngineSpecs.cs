using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Tests.Shared;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Sqids;

namespace Valtuutus.Data.SqlServer.Tests;


[Collection("SqlServerSpec")]
public sealed class DataEngineSpecs : DataSpecificDataEngineSpecs
{

    public DataEngineSpecs(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }
    
    [Fact]
    public async Task WritingData_ShouldAssociateRelationWithTransactionId()
    {
        
        // act
        var dataEngine = _provider.GetRequiredService<DataEngine>();
        var snapToken = await dataEngine.Write([new RelationTuple("project", "1", "member", "user", "1")], [], default);
        var decoder = _provider.GetRequiredService<SqidsEncoder<long>>();
        var transactionId = decoder.Decode(snapToken.Value).Single();

        // assert
        using var db = _fixture.DbFactory();
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
        var dataEngine = _provider.GetRequiredService<DataEngine>();
        
        // act
        var decoder = _provider.GetRequiredService<SqidsEncoder<long>>();
        var newSnapToken = await dataEngine.Delete(new DeleteFilter
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
        using var db = _fixture.DbFactory();
        
        var newTransactionId = decoder.Decode(newSnapToken.Value).Single();
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

    protected override void AddSpecificProvider(IValtuutusDataBuilder builder)
    {
        builder.AddSqlServer(_ => _fixture.DbFactory);
    }

    protected override async Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples()
    {
        using var db = _fixture.DbFactory();
        var relations = (await db.QueryAsync<RelationTuple>("""
            SELECT  entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id, 
                    subject_relation from relation_tuples
            """)).ToArray();
        var attributes =
            (await db.QueryAsync<AttributeTuple>("select entity_type, entity_id, attribute,value from attributes")).ToArray();
        
        return (relations, attributes);

    }
}