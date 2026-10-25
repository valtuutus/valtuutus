using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests
{
    // Keys match only when their bytes are equal: a trailing space or a different case names a
    // different key, on bound parameters, JSON_TABLE joins and the unique key alike.
    public abstract class ExactKeyComparisonSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        private ServiceProvider _provider = default!;

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

        private Task<SnapToken> WriteAliceMember() =>
            _provider.GetRequiredService<IDataWriterProvider>()
                .Write([new RelationTuple("team", "t1", "member", "user", "alice")], [], default);

        [Theory]
        [InlineData("alice", true)]
        [InlineData("alice ", false)]
        [InlineData("Alice", false)]
        public async Task Check_matches_only_the_exact_subject_id(string subjectId, bool allowed)
        {
            var token = await WriteAliceMember();
            var engine = _provider.GetRequiredService<ICheckEngine>();

            var result = await engine.Check(new CheckRequest("team", "t1", "member", "user", subjectId, snapToken: token), default);

            result.Allowed.Should().Be(allowed);
        }

        [Fact]
        public async Task LookupEntity_with_a_trailing_space_finds_nothing()
        {
            var token = await WriteAliceMember();
            var engine = _provider.GetRequiredService<ILookupEntityEngine>();

            var page = await engine.LookupEntity(new LookupEntityRequest("team", "member", "user", "alice ") { SnapToken = token }, default);

            page.EntityIds.Should().BeEmpty();
        }

        [Fact]
        public async Task Subject_id_arrays_match_only_exact_ids()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            var token = await writer.Write(
            [
                new RelationTuple("team", "t1", "member", "user", "alice"),
                new RelationTuple("team", "t2", "member", "user", "bob"),
            ], [], default);
            var reader = _provider.GetRequiredService<IDataReaderProvider>();

            using var tuples = await reader.GetRelationsWithSubjectsIds(
                new EntityRelationFilter { EntityType = "team", Relation = "member", SnapToken = token },
                ["alice ", "bob"], "user", "", null, default);

            tuples.Select(t => t.SubjectId).Should().Equal("bob");
        }

        [Fact]
        public async Task Attribute_names_differing_by_a_trailing_space_are_distinct_keys()
        {
            var writer = _provider.GetRequiredService<IDataWriterProvider>();

            await writer.Write([],
            [
                new AttributeTuple("team", "t1", "x", JsonValue.Create(true)),
                new AttributeTuple("team", "t1", "x ", JsonValue.Create(false)),
            ], default);

            await using var db = (MySqlConnection)fixture.DbFactory();
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM attributes WHERE deleted_tx_id IS NULL";
            Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(2);
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class ExactKeyComparisonSpecs(MySqlFixture fixture) : ExactKeyComparisonSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class ExactKeyComparisonSpecs(MariaDbFixture fixture) : ExactKeyComparisonSpecsBase(fixture);
}
