using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// LookupSubject V2 runs each step's queries in one round trip, and a lookup without a caller
    /// token resolves the latest token inside its first round trip; every later step of that request
    /// still reads that one token.
    /// </summary>
    public abstract class LookupSubjectV2RoundTripSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        // edit: two sibling direct relations, one query. review: a direct relation plus a terminal
        // userset, two queries in one step. view: a non-terminal userset, one step per group hop.
        private const string Schema = """
            entity user {}
            entity team {
                relation member @user;
            }
            entity group {
                relation member @user @group#member;
            }
            entity project {
                relation owner @user;
                relation editor @user;
                relation reviewer @team#member;
                relation viewer @group#member;
                permission edit := owner or editor;
                permission review := owner or reviewer;
                permission view := viewer;
            }
            """;

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task Tokenless_one_step_lookup_resolves_the_token_in_its_only_round_trip()
        {
            await Write(new RelationTuple("project", "p1", "owner", "user", "alice"));

            var counter = new RoundTripCounter();
            await using var sp = BuildServices(counter);
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

            var subjects = (await engine.Lookup(new LookupSubjectRequest("project", "edit", "user", "p1"), default)).SubjectIds;

            subjects.Should().BeEquivalentTo("alice");
            counter.Count.Should().Be(1);
        }

        [Fact]
        public async Task A_step_with_several_queries_is_one_round_trip()
        {
            await Write(new RelationTuple("project", "p1", "owner", "user", "alice"));
            await Write(new RelationTuple("project", "p1", "reviewer", "team", "t1", "member"));
            var token = await Write(new RelationTuple("team", "t1", "member", "user", "bob"));

            var counter = new RoundTripCounter();
            await using var sp = BuildServices(counter);
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

            var subjects = (await engine.Lookup(
                new LookupSubjectRequest("project", "review", "user", "p1") { SnapToken = token }, default)).SubjectIds;

            subjects.Should().BeEquivalentTo("alice", "bob");
            counter.Count.Should().Be(1);
        }

        [Fact]
        public async Task Write_committed_between_steps_is_invisible_to_the_running_lookup()
        {
            await Write(new RelationTuple("project", "p1", "viewer", "group", "g1", "member"));
            await Write(new RelationTuple("group", "g1", "member", "group", "g2", "member"));
            await Write(new RelationTuple("group", "g2", "member", "user", "bob"));

            var counter = new RoundTripCounter();
            var wroteMidRequest = false;
            await using var sp = BuildServices(counter, beforeRoundTrip: async roundTrip =>
            {
                if (roundTrip != 2) return;
                // Step 1 already pinned the token; this grant commits after it, before step 2 reads g1.
                await Write(new RelationTuple("group", "g1", "member", "user", "alice"));
                wroteMidRequest = true;
            }, hops: 1);
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

            var duringWrite = (await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1"), default)).SubjectIds;
            var afterWrite = (await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1"), default)).SubjectIds;

            wroteMidRequest.Should().BeTrue("the lookup must have needed a second step for this spec to mean anything");
            duringWrite.Should().BeEquivalentTo(["bob"], "the grant committed after the request's token was resolved");
            afterWrite.Should().BeEquivalentTo(["alice", "bob"], "a new request resolves a new token that includes the grant");
        }

        [Theory]
        [InlineData(1, 4)]
        [InlineData(2, 2)]
        [InlineData(4, 1)]
        public async Task Nested_userset_levels_share_round_trips(int hops, int roundTrips)
        {
            await Write(new RelationTuple("group", "g0", "member", "user", "u0"));
            await Write(new RelationTuple("group", "g0", "member", "group", "g1", "member"));
            await Write(new RelationTuple("group", "g1", "member", "user", "u1"));
            await Write(new RelationTuple("group", "g1", "member", "group", "g2", "member"));
            await Write(new RelationTuple("group", "g2", "member", "user", "u2"));
            await Write(new RelationTuple("group", "g2", "member", "group", "g3", "member"));
            await Write(new RelationTuple("group", "g3", "member", "user", "u3"));

            var counter = new RoundTripCounter();
            await using var sp = BuildServices(counter, hops: hops);
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

            var subjects = (await engine.Lookup(new LookupSubjectRequest("group", "member", "user", "g0"), default)).SubjectIds;

            subjects.Should().BeEquivalentTo("u0", "u1", "u2", "u3");
            counter.Count.Should().Be(roundTrips);
        }

        [Fact]
        public async Task Explicit_token_lookups_keep_binding_that_token()
        {
            var before = await Write(new RelationTuple("project", "p1", "owner", "user", "bob"));
            await Write(new RelationTuple("project", "p1", "editor", "user", "alice"));

            var counter = new RoundTripCounter();
            await using var sp = BuildServices(counter);
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

            // A tokenless lookup first leaves its resolved token on the pooled connection; the
            // explicit-token request must still read only its own snapshot.
            (await engine.Lookup(new LookupSubjectRequest("project", "edit", "user", "p1"), default)).SubjectIds
                .Should().BeEquivalentTo("alice", "bob");
            (await engine.Lookup(new LookupSubjectRequest("project", "edit", "user", "p1") { SnapToken = before }, default)).SubjectIds
                .Should().BeEquivalentTo("bob");
        }

        private ServiceProvider BuildServices(RoundTripCounter counter,
            Func<int, Task>? beforeRoundTrip = null, int? hops = null)
        {
            counter.BeforeRoundTrip = beforeRoundTrip;
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            // Two hops per query unless a spec pins its own, so the nested specs need more than one step.
            services.AddMySql(_ => fixture.DbFactory, fixture.Options with { LookupHopsPerQuery = hops ?? 2 })
                .AddConcurrentQueryLimit(3);
            services.AddSingleton(counter);
            services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
                new CountingReaderProvider(ActivatorUtilities.CreateInstance<MySqlDataReaderProvider>(sp), counter)));
            services.Replace(ServiceDescriptor.Singleton<IRelationalBatchOps>(sp =>
                new CountingBatchOps(
                    new MySqlBatchOps(sp.GetRequiredService<DbConnectionFactory>(),
                        sp.GetRequiredService<ValtuutusDataOptions>(),
                        sp.GetRequiredService<ValtuutusMySqlOptions>()),
                    counter)));
            return services.BuildServiceProvider();
        }

        private async Task<SnapToken> Write(RelationTuple tuple)
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options);
            await using var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write([tuple], [], default);
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class LookupSubjectV2RoundTripSpecs(MySqlFixture fixture) : LookupSubjectV2RoundTripSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class LookupSubjectV2RoundTripSpecs(MariaDbFixture fixture) : LookupSubjectV2RoundTripSpecsBase(fixture);
}
