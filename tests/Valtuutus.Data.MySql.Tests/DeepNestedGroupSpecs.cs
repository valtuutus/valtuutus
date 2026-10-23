using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// A nested group chain fifteen levels deep, with a cycle inside it, answers the same at any
    /// number of levels per query: shallower than the chain, deeper than the cycle, and deeper than
    /// the chain itself. The cycle can only end through the level cap, and its repeated groups must
    /// still come back once.
    /// </summary>
    public abstract class DeepNestedGroupSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        private const string Schema = """
            entity user {}
            entity group {
                relation member @user @group#member;
            }
            entity project {
                relation viewer @group#member;
                permission view := viewer;
            }
            """;

        private const int ChainLength = 15;

        // Deep enough for the whole chain and its cycle.
        private const int Depth = 64;

        private static string Group(int i) => $"g{i:00}";

        // g00 holds g01's members, g01 holds g02's, and so on down to g15. alice is in g15, at the
        // bottom; bob is in g07. g12 also holds g05's members, closing the cycle g05 -> ... -> g12 -> g05,
        // which carries bob into g08..g12.
        private static RelationTuple[] Tuples() =>
        [
            .. Enumerable.Range(0, ChainLength)
                .Select(i => new RelationTuple("group", Group(i), "member", "group", Group(i + 1), "member")),
            new("group", Group(12), "member", "group", Group(5), "member"),
            new("group", Group(ChainLength), "member", "user", "alice"),
            new("group", Group(7), "member", "user", "bob"),
            new("project", "p1", "viewer", "group", Group(12), "member"),
            new("project", "p2", "viewer", "group", Group(13), "member"),
            new("project", "p3", "viewer", "group", Group(0), "member"),
        ];

        public static TheoryData<int> HopsPerQuery() => new() { 1, 2, 3, 12, 20 };

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        private ServiceProvider Services(int hopsPerQuery)
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options with { LookupHopsPerQuery = hopsPerQuery });
            return services.BuildServiceProvider();
        }

        private async Task<SnapToken> Seed()
        {
            await using var services = Services(1);
            return await services.GetRequiredService<IDataWriterProvider>().Write(Tuples(), [], default);
        }

        [Theory]
        [MemberData(nameof(HopsPerQuery))]
        public async Task LookupEntity_reads_the_whole_chain_through_the_cycle(int hopsPerQuery)
        {
            var token = await Seed();
            await using var services = Services(hopsPerQuery);
            await using var scope = services.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

            foreach (var snapToken in new SnapToken?[] { null, token })
            {
                (await Lookup("group", "member", "alice")).Should()
                    .BeEquivalentTo(Enumerable.Range(0, ChainLength + 1).Select(Group));
                (await Lookup("group", "member", "bob")).Should()
                    .BeEquivalentTo(Enumerable.Range(0, 13).Select(Group));
                (await Lookup("project", "view", "alice")).Should().BeEquivalentTo(["p1", "p2", "p3"]);
                (await Lookup("project", "view", "bob")).Should().BeEquivalentTo(["p1", "p3"]);

                async Task<IEnumerable<string>> Lookup(string entityType, string permission, string user) =>
                    (await engine.LookupEntity(new LookupEntityRequest(entityType, permission, "user", user)
                    {
                        SnapToken = snapToken, Depth = Depth
                    }, default)).EntityIds;
            }
        }

        [Theory]
        [MemberData(nameof(HopsPerQuery))]
        public async Task LookupSubject_reads_the_whole_chain_through_the_cycle(int hopsPerQuery)
        {
            var token = await Seed();
            await using var services = Services(hopsPerQuery);
            await using var scope = services.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

            foreach (var snapToken in new SnapToken?[] { null, token })
            {
                (await Lookup("group", Group(0))).Should().BeEquivalentTo(["alice", "bob"]);
                (await Lookup("group", Group(5))).Should().BeEquivalentTo(["alice", "bob"]);
                (await Lookup("group", Group(12))).Should().BeEquivalentTo(["alice", "bob"]);
                (await Lookup("group", Group(13))).Should().BeEquivalentTo(["alice"]);
                (await Lookup("group", Group(ChainLength))).Should().BeEquivalentTo(["alice"]);
                (await Lookup("project", "p1")).Should().BeEquivalentTo(["alice", "bob"]);
                (await Lookup("project", "p2")).Should().BeEquivalentTo(["alice"]);
                (await Lookup("project", "p3")).Should().BeEquivalentTo(["alice", "bob"]);

                async Task<IEnumerable<string>> Lookup(string entityType, string entityId) =>
                    (await engine.Lookup(new LookupSubjectRequest(entityType, entityType == "group" ? "member" : "view",
                        "user", entityId)
                    {
                        SnapToken = snapToken, Depth = Depth
                    }, default)).SubjectIds;
            }
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class DeepNestedGroupSpecs(MySqlFixture fixture) : DeepNestedGroupSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class DeepNestedGroupSpecs(MariaDbFixture fixture) : DeepNestedGroupSpecsBase(fixture);
}
