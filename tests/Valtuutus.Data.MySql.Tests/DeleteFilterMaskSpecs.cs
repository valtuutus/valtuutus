using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests
{
    public abstract class DeleteFilterMaskSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        private ServiceProvider _provider = default!;

        private static readonly RelationTuple OwnerP1 = new("project", "p1", "owner", "user", "u1");
        private static readonly RelationTuple MemberP1 = new("project", "p1", "member", "user", "u2");
        private static readonly RelationTuple MemberP2 = new("project", "p2", "member", "user", "u1");
        private static readonly RelationTuple GroupMemberP2 = new("project", "p2", "member", "group", "g1", "member");
        private static readonly RelationTuple OwnerP3 = new("project", "p3", "owner", "user", "u3");
        private static readonly RelationTuple MemberG1 = new("group", "g1", "member", "user", "u1");

        private static readonly RelationTuple[] All = [OwnerP1, MemberP1, MemberP2, GroupMemberP2, OwnerP3, MemberG1];

        public Task InitializeAsync()
        {
            var services = new ServiceCollection().AddValtuutusCore(TestsConsts.DefaultSchema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options);
            _provider = services.BuildServiceProvider();
            return fixture.ResetDatabaseAsync();
        }

        public async Task DisposeAsync()
        {
            await _provider.DisposeAsync();
        }

        private async Task<SnapToken> Delete(DeleteFilter filter)
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            await writer.Write(All, [], default);
            return await writer.Delete(filter, default);
        }

        [Fact]
        public async Task Filters_with_different_masks_delete_exactly_their_matches()
        {
            await Delete(new DeleteFilter
            {
                Relations =
                [
                    new DeleteRelationsFilter
                    {
                        EntityType = "project", EntityId = "p1", Relation = "owner",
                        SubjectType = "user", SubjectId = "u1", SubjectRelation = ""
                    },
                    new DeleteRelationsFilter { EntityType = "project", EntityId = "p3" },
                    new DeleteRelationsFilter { SubjectType = "user", SubjectId = "u2" },
                    new DeleteRelationsFilter { EntityType = "group", EntityId = "g1", Relation = "member" },
                ]
            });

            (await LiveRelations()).Should().BeEquivalentTo([MemberP2, GroupMemberP2]);
        }

        [Fact]
        public async Task Subject_only_filter_deletes_every_tuple_of_that_subject()
        {
            await Delete(new DeleteFilter
            {
                Relations = [new DeleteRelationsFilter { SubjectType = "user", SubjectId = "u1" }]
            });

            (await LiveRelations()).Should().BeEquivalentTo([MemberP1, GroupMemberP2, OwnerP3]);
        }

        [Fact]
        public async Task Empty_subject_relation_matches_only_direct_tuples()
        {
            await Delete(new DeleteFilter
            {
                Relations = [new DeleteRelationsFilter { EntityType = "project", EntityId = "p2", SubjectRelation = "" }]
            });

            (await LiveRelations()).Should().BeEquivalentTo([OwnerP1, MemberP1, GroupMemberP2, OwnerP3, MemberG1]);
        }

        [Fact]
        public async Task Filters_sharing_a_mask_are_applied_together()
        {
            await Delete(new DeleteFilter
            {
                Relations =
                [
                    new DeleteRelationsFilter { EntityType = "project", EntityId = "p1" },
                    new DeleteRelationsFilter { EntityType = "project", EntityId = "p3" },
                ]
            });

            (await LiveRelations()).Should().BeEquivalentTo([MemberP2, GroupMemberP2, MemberG1]);
        }

        [Fact]
        public async Task All_null_filter_tombstones_every_live_tuple()
        {
            var deleteToken = await Delete(new DeleteFilter { Relations = [new DeleteRelationsFilter()] });

            (await LiveRelations()).Should().BeEmpty();
            (await CountTombstones("relation_tuples", deleteToken)).Should().Be(All.Length);
        }

        [Fact]
        public async Task Filter_values_longer_than_their_column_match_nothing()
        {
            var id = new string('a', 64);
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            await writer.Write([new RelationTuple("project", id, "owner", "user", "u1")],
                [new AttributeTuple("project", id, "name", System.Text.Json.Nodes.JsonValue.Create("one"))], default);

            await writer.Delete(new DeleteFilter
            {
                Relations = [new DeleteRelationsFilter { EntityType = "project", EntityId = id + "x" }],
                Attributes = [new DeleteAttributesFilter { EntityType = "project", EntityId = id + "x" }],
            }, default);

            (await LiveRelations()).Should().ContainSingle();
            (await LiveAttributeKeys()).Should().ContainSingle();
        }

        [Fact]
        public async Task Attribute_filters_with_and_without_a_name_delete_their_matches()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            await writer.Write([],
            [
                new AttributeTuple("project", "p1", "name", System.Text.Json.Nodes.JsonValue.Create("one")),
                new AttributeTuple("project", "p1", "public", System.Text.Json.Nodes.JsonValue.Create(true)),
                new AttributeTuple("project", "p2", "name", System.Text.Json.Nodes.JsonValue.Create("two")),
                new AttributeTuple("project", "p3", "name", System.Text.Json.Nodes.JsonValue.Create("three")),
            ], default);

            var deleteToken = await writer.Delete(new DeleteFilter
            {
                Attributes =
                [
                    new DeleteAttributesFilter { EntityType = "project", EntityId = "p1", Attribute = "name" },
                    new DeleteAttributesFilter { EntityType = "project", EntityId = "p2" },
                ]
            }, default);

            (await LiveAttributeKeys()).Should().BeEquivalentTo(["project:p1#public", "project:p3#name"]);
            (await CountTombstones("attributes", deleteToken)).Should().Be(2);
        }

        private async Task<MySqlConnection> Open()
        {
            var db = (MySqlConnection)fixture.DbFactory();
            await db.OpenAsync();
            return db;
        }

        private async Task<List<RelationTuple>> LiveRelations()
        {
            await using var db = await Open();
            await using var command = db.CreateCommand();
            command.CommandText =
                "SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM relation_tuples WHERE deleted_tx_id IS NULL";
            await using var reader = await command.ExecuteReaderAsync();
            var tuples = new List<RelationTuple>();
            while (await reader.ReadAsync())
                tuples.Add(new RelationTuple(Key(reader, 0), Key(reader, 1), Key(reader, 2),
                    Key(reader, 3), Key(reader, 4), Key(reader, 5)));
            return tuples;
        }

        private async Task<List<string>> LiveAttributeKeys()
        {
            await using var db = await Open();
            await using var command = db.CreateCommand();
            command.CommandText =
                "SELECT CONCAT(entity_type, ':', entity_id, '#', attribute) FROM attributes WHERE deleted_tx_id IS NULL";
            await using var reader = await command.ExecuteReaderAsync();
            var keys = new List<string>();
            while (await reader.ReadAsync())
                keys.Add(Key(reader, 0));
            return keys;
        }

        // Key columns are VARBINARY and hold UTF-8.
        private static string Key(System.Data.Common.DbDataReader reader, int ordinal) =>
            System.Text.Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(ordinal));

        private async Task<long> CountTombstones(string table, SnapToken deleteToken)
        {
            await using var db = await Open();
            await using var command = db.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE deleted_tx_id = @tx";
            command.Parameters.AddWithValue("@tx", deleteToken.Value);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class DeleteFilterMaskSpecs(MySqlFixture fixture) : DeleteFilterMaskSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class DeleteFilterMaskSpecs(MariaDbFixture fixture) : DeleteFilterMaskSpecsBase(fixture);
}
