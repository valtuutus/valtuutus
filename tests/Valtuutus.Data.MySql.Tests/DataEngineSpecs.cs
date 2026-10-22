using System.Data.Common;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests
{
    public abstract class DataEngineSpecsBase(MySqlFixtureBase fixture, MySqlServerType serverType)
        : BaseDataEngineSpecs(fixture)
    {
        // The shared base builds its provider before it stores the fixture, so the options come from
        // the server type alone and the connection factory is read only when a connection opens.
        private readonly ValtuutusMySqlOptions _options = new(serverType);

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => ((IWithDbConnectionFactory)Fixture).DbFactory, _options);

        private async Task<MySqlConnection> Open()
        {
            var db = (MySqlConnection)((IWithDbConnectionFactory)Fixture).DbFactory();
            await db.OpenAsync();
            return db;
        }

        [Fact]
        public async Task WritingData_ShouldAssociateRelationWithTransactionId()
        {
            // act
            var provider = Provider.GetRequiredService<IDataWriterProvider>();
            var snapToken = await provider.Write([new RelationTuple("project", "1", "member", "user", "1")], [], default);
            var transactionId = Ulid.Parse(snapToken.Value);

            // assert
            await using var db = await Open();

            await using var relationCountCommand = db.CreateCommand();
            relationCountCommand.CommandText = "SELECT COUNT(*) FROM relation_tuples WHERE created_tx_id = @id";
            relationCountCommand.Parameters.AddWithValue("@id", transactionId.ToString());
            var relationCount = Convert.ToInt64(await relationCountCommand.ExecuteScalarAsync());

            relationCount.Should().Be(1);

            await using var existsCommand = db.CreateCommand();
            existsCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM transactions WHERE id = @id)";
            existsCommand.Parameters.AddWithValue("@id", transactionId.ToString());
            var exists = Convert.ToBoolean(await existsCommand.ExecuteScalarAsync());

            exists.Should().BeTrue();
        }

        [Fact]
        public async Task WritingData_TwiceOnSameConnection_ShouldNotThrow()
        {
            // arrange
            var provider = Provider.GetRequiredService<IDbDataWriterProvider>();
            await using var db = await Open();

            // act
            await provider.Write(db, [], [new AttributeTuple("project", "1", "name", JsonValue.Create("foo")!)], default);
            var act = () => provider.Write(db, [], [new AttributeTuple("project", "2", "name", JsonValue.Create("bar")!)], default);

            // assert
            await act.Should().NotThrowAsync();
        }

        [Fact]
        public async Task WritingData_TwiceInSameTransaction_ShouldNotThrow()
        {
            // arrange
            var provider = Provider.GetRequiredService<IDbDataWriterProvider>();
            await using var db = await Open();
            await using var transaction = await db.BeginTransactionAsync();

            // act
            await provider.Write(db, transaction, [], [new AttributeTuple("project", "1", "name", JsonValue.Create("foo")!)], default);
            var act = () => provider.Write(db, transaction, [], [new AttributeTuple("project", "2", "name", JsonValue.Create("bar")!)], default);

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
                Relations =
                [
                    new DeleteRelationsFilter
                    {
                        EntityType = "project",
                        EntityId = "1",
                        Relation = "member",
                        SubjectType = "user",
                        SubjectId = "1"
                    }
                ]
            }, default);

            // assert
            await using var db = await Open();

            var newTransactionId = Ulid.Parse(newSnapToken.Value);
            // new transaction should exist
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM transactions WHERE id = @id)";
            command.Parameters.AddWithValue("@id", newTransactionId.ToString());
            var newTransaction = Convert.ToBoolean(await command.ExecuteScalarAsync());

            newTransaction.Should().BeTrue();
        }

        [Fact]
        public async Task DeletingData_BatchWithMixedAttributeFilters_ShouldScopeAttributeFilterPerRow()
        {
            // arrange: filter[0] deletes every attribute of project/1, filter[1] deletes only "name"
            // for project/2. A per-row Attribute constraint must stay on its own row of the batch.
            var writer = Provider.GetRequiredService<IDataWriterProvider>();
            await writer.Write([], [
                new AttributeTuple("project", "1", "name", JsonValue.Create("a")!),
                new AttributeTuple("project", "1", "public", JsonValue.Create(true)!),
                new AttributeTuple("project", "2", "name", JsonValue.Create("b")!),
                new AttributeTuple("project", "2", "public", JsonValue.Create(true)!)
            ], default);

            // act
            await writer.Delete(new DeleteFilter
            {
                Attributes =
                [
                    new DeleteAttributesFilter { EntityType = "project", EntityId = "1" },
                    new DeleteAttributesFilter { EntityType = "project", EntityId = "2", Attribute = "name" }
                ]
            }, default);

            // assert
            var (_, attributes) = await GetCurrentTuples();
            attributes.Select(a => (a.EntityId, a.Attribute)).Should().BeEquivalentTo(new[]
            {
                ("2", "public")
            });
        }

        // Key columns are VARBINARY and hold UTF-8.
        private static string Key(DbDataReader reader, int ordinal) =>
            Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(ordinal));

        protected override async Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples()
        {
            await using var db = await Open();

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
                        Key(reader, 0), Key(reader, 1), Key(reader, 2),
                        Key(reader, 3), Key(reader, 4), Key(reader, 5)));
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
                        Key(reader, 0), Key(reader, 1), Key(reader, 2),
                        JsonNode.Parse(reader.GetString(3))!.AsValue()));
                }
            }

            return (relations.ToArray(), attributes.ToArray());
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class DataEngineSpecs(MySqlFixture fixture) : DataEngineSpecsBase(fixture, MySqlServerType.MySql);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class DataEngineSpecs(MariaDbFixture fixture) : DataEngineSpecsBase(fixture, MySqlServerType.MariaDb);
}
