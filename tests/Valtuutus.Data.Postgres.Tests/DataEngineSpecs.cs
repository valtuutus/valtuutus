using Valtuutus.Core;
using Valtuutus.Core.Data;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;


[Collection("PostgreSqlSpec")]
public class DataEngineSpecs : BaseDataEngineSpecs
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddPostgres(_ =>  ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
    public DataEngineSpecs(PostgresFixture fixture) : base(fixture){}
    
    [Fact]
    public async Task WritingData_ShouldAssociateRelationWithTransactionId()
    {
        // act
        var provider = Provider.GetRequiredService<IDataWriterProvider>();
        var snapToken = await provider.Write([new RelationTuple("project", "1", "member", "user", "1")], [], default);
        var transactionId = Ulid.Parse(snapToken.Value);

        // assert
        using var db = ((IWithDbConnectionFactory)Fixture).DbFactory();
        var relationCount = await db.ExecuteScalarAsync<bool>("SELECT (SELECT COUNT(*) FROM public.relation_tuples WHERE created_tx_id = @id) = 1", 
            new { id = transactionId });
        
        relationCount.Should().BeTrue();
        
        var exists = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.transactions WHERE id = @id)", 
            new { id = transactionId });
        
        exists.Should().BeTrue();
    }
    
    [Fact]
    public async Task WritingData_TwiceOnSameConnection_ShouldNotThrow()
    {
        // arrange
        var provider = Provider.GetRequiredService<IDbDataWriterProvider>();
        await using var db = (Npgsql.NpgsqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await db.OpenAsync();

        // act
        await provider.Write(db, [], [new AttributeTuple("project", "1", "name", System.Text.Json.Nodes.JsonValue.Create("foo")!)], default);
        var act = () => provider.Write(db, [], [new AttributeTuple("project", "2", "name", System.Text.Json.Nodes.JsonValue.Create("bar")!)], default);

        // assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WritingData_TwiceInSameTransaction_ShouldNotThrow()
    {
        // arrange
        var provider = Provider.GetRequiredService<IDbDataWriterProvider>();
        await using var db = (Npgsql.NpgsqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await db.OpenAsync();
        await using var transaction = await db.BeginTransactionAsync();

        // act
        await provider.Write(db, transaction, [], [new AttributeTuple("project", "1", "name", System.Text.Json.Nodes.JsonValue.Create("foo")!)], default);
        var act = () => provider.Write(db, transaction, [], [new AttributeTuple("project", "2", "name", System.Text.Json.Nodes.JsonValue.Create("bar")!)], default);

        // assert
        await act.Should().NotThrowAsync();
        await transaction.CommitAsync();
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
        var newTransaction = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.transactions WHERE id = @id)", 
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
                                                                    subject_relation from public.relation_tuples where deleted_tx_id is null
                                                            """)).ToArray();
        var attributes =
            (await db.QueryAsync<AttributeTuple>("select entity_type, entity_id, attribute,value from public.attributes where deleted_tx_id is null")).ToArray();
        
        return (relations, attributes);

    }

}