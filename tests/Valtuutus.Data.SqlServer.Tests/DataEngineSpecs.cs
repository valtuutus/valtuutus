using Valtuutus.Core;
using Valtuutus.Core.Data;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Db;
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
    public async Task WritingData_TwiceOnSameConnection_ShouldNotThrow()
    {
        // arrange
        var provider = Provider.GetRequiredService<IDbDataWriterProvider>();
        await using var db = (Microsoft.Data.SqlClient.SqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
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
        await using var db = (Microsoft.Data.SqlClient.SqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await db.OpenAsync();
        var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await db.BeginTransactionAsync();

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

    [Fact]
    public async Task DeletingData_BatchWithMixedAttributeFilters_ShouldScopeAttributeFilterPerRow()
    {
        // arrange: filter[0] wildcard-deletes every attribute of project/1, filter[1] deletes only
        // "name" for project/2. A per-row Attribute constraint must not leak onto other rows in the
        // same batch delete (regression for a bug in the old SqlBuilder-based OrWhere/Where mixing).
        var writer = Provider.GetRequiredService<IDataWriterProvider>();
        await writer.Write([], [
            new AttributeTuple("project", "1", "name", System.Text.Json.Nodes.JsonValue.Create("a")!),
            new AttributeTuple("project", "1", "public", System.Text.Json.Nodes.JsonValue.Create(true)!),
            new AttributeTuple("project", "2", "name", System.Text.Json.Nodes.JsonValue.Create("b")!),
            new AttributeTuple("project", "2", "public", System.Text.Json.Nodes.JsonValue.Create(true)!)
        ], default);

        // act
        await writer.Delete(new DeleteFilter
        {
            Attributes = new[]
            {
                new DeleteAttributesFilter { EntityType = "project", EntityId = "1" },
                new DeleteAttributesFilter { EntityType = "project", EntityId = "2", Attribute = "name" }
            }
        }, default);

        // assert
        var (_, attributes) = await GetCurrentTuples();
        attributes.Select(a => (a.EntityId, a.Attribute)).Should().BeEquivalentTo(new[]
        {
            ("2", "public")
        });
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