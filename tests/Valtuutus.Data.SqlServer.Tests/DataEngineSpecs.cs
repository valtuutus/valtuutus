using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using FluentAssertions;
using Microsoft.Data.SqlClient;
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
        await using var db = (SqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await db.OpenAsync();

        await using var relationCountCommand = db.CreateCommand();
        relationCountCommand.CommandText = "SELECT CASE WHEN (SELECT COUNT(*) FROM relation_tuples WHERE created_tx_id = @id) = 1 THEN 1 ELSE 0 END";
        relationCountCommand.Parameters.AddWithValue("id", transactionId.ToString());
        var relationCount = (int)(await relationCountCommand.ExecuteScalarAsync())! == 1;

        await using var existsCommand = db.CreateCommand();
        existsCommand.CommandText = """
                                     SELECT
                                     CASE
                                         WHEN EXISTS(SELECT 1 FROM transactions WHERE id = @id)
                                              THEN 1
                                         ELSE 0
                                     END
                                     """;
        existsCommand.Parameters.AddWithValue("id", transactionId.ToString());
        var exists = (int)(await existsCommand.ExecuteScalarAsync())! == 1;

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
        await using var db = (SqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await db.OpenAsync();

        var newTransactionId = Ulid.Parse(newSnapToken.Value);
        // new transaction should exist
        await using var command = db.CreateCommand();
        command.CommandText = """
                              SELECT
                              CASE
                                  WHEN EXISTS(SELECT 1 FROM transactions WHERE id = @id)
                                       THEN 1
                                  ELSE 0
                              END
                              """;
        command.Parameters.AddWithValue("id", newTransactionId.ToString());
        var newTransaction = (int)(await command.ExecuteScalarAsync())! == 1;

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
        await using var db = (SqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
        await db.OpenAsync();

        var relations = new List<RelationTuple>();
        await using (var command = db.CreateCommand())
        {
            command.CommandText = """
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM relation_tuples WHERE deleted_tx_id IS NULL
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                relations.Add(new RelationTuple(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }
        }

        var attributes = new List<AttributeTuple>();
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT entity_type, entity_id, attribute, value FROM attributes WHERE deleted_tx_id IS NULL";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                attributes.Add(new AttributeTuple(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    JsonNode.Parse(reader.GetString(3))!.AsValue()));
            }
        }

        return (relations.ToArray(), attributes.ToArray());
    }
}