﻿using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Tests.Shared;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Valtuutus.Data.Postgres.Tests;


[Collection("PostgreSqlSpec")]
public class DataEngineSpecs : DataSpecificDataEngineSpecs
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddPostgres(_ =>  ((IWithDbConnectionFactory)_fixture).DbFactory);
    }
    public DataEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }
   
    
    [Fact]
    public async Task WritingData_ShouldAssociateRelationWithTransactionId()
    {
        // act
        var dataEngine = _provider.GetRequiredService<DataEngine>();
        var snapToken = await dataEngine.Write([new RelationTuple("project", "1", "member", "user", "1")], [], default);
        var transactionId = Ulid.Parse(snapToken.Value);

        // assert
        using var db = ((IWithDbConnectionFactory)_fixture).DbFactory();
        var relationCount = await db.ExecuteScalarAsync<bool>("SELECT (SELECT COUNT(*) FROM public.relation_tuples WHERE created_tx_id = @id) = 1", 
            new { id = transactionId });
        
        relationCount.Should().BeTrue();
        
        var exists = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.transactions WHERE id = @id)", 
            new { id = transactionId });
        
        exists.Should().BeTrue();
    }
    
    [Fact]
    public async Task DeletingData_ShouldReturnTransactionId()
    {
        // arrange
        var dataEngine = _provider.GetRequiredService<DataEngine>();
        
        // act
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
        using var db = ((IWithDbConnectionFactory)_fixture).DbFactory();

        var newTransactionId = Ulid.Parse(newSnapToken.Value);
        // new transaction should exist
        var newTransaction = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.transactions WHERE id = @id)", 
            new { id = newTransactionId });
        
        newTransaction.Should().BeTrue();
    }
    
    protected override async Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples()
    {
        using var db = ((IWithDbConnectionFactory)_fixture).DbFactory();
        var relations = (await db.QueryAsync<RelationTuple>("""
                                                            SELECT  entity_type,
                                                                    entity_id,
                                                                    relation,
                                                                    subject_type,
                                                                    subject_id,
                                                                    subject_relation from public.relation_tuples
                                                            """)).ToArray();
        var attributes =
            (await db.QueryAsync<AttributeTuple>("select entity_type, entity_id, attribute,value from public.attributes")).ToArray();
        
        return (relations, attributes);

    }

}