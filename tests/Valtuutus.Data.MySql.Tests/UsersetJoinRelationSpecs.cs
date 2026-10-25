using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// End-to-end correctness and round-trip proof for the userset 2-hop join fast path: a
    /// DirectRelationNode with a userset target answered by one UsersetJoinOp round trip instead of
    /// HasDirectRelation-then-GetIndirectRelations-fan-out.
    /// </summary>
    public abstract class UsersetJoinRelationSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        private const string Schema = """
            entity user {}
            entity group {
                relation member @user;
            }
            entity folder {
                relation owner @user @group#member;
            }
            """;

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        private async Task<SnapToken> Seed(RelationTuple[] tuples)
        {
            var seedServices = new ServiceCollection().AddValtuutusCore(Schema);
            seedServices.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);
            await using var seedProvider = seedServices.BuildServiceProvider();
            await using var seedScope = seedProvider.CreateAsyncScope();
            var writer = seedScope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
            return await writer.Write(tuples, [], default);
        }

        [Theory]
        [InlineData("f1", "direct-hit", true)]   // folder:f1#owner@user:direct-hit, direct membership
        [InlineData("f2", "join-hit", true)]     // folder:f2#owner@group:g1#member, group:g1#member@user:join-hit
        [InlineData("f2", "someone-else", false)] // present group, subject not a member of it
        [InlineData("f3", "anyone", false)]      // folder:f3 has no owner tuples at all
        public async Task Check_matches_expected_result(string entityId, string subjectId, bool expected)
        {
            var snapToken = await Seed(
            [
                new RelationTuple("folder", "f1", "owner", "user", "direct-hit"),
                new RelationTuple("folder", "f2", "owner", "group", "g1", "member"),
                new RelationTuple("group", "g1", "member", "user", "join-hit"),
            ]);

            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);
            await using var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

            var result = (await engine.Check(
                new CheckRequest("folder", entityId, "owner", "user", subjectId, snapToken: snapToken), default)).Allowed;

            result.Should().Be(expected);
        }

        [Fact]
        public async Task Userset_join_hit_costs_exactly_one_round_trip()
        {
            var snapToken = await Seed(
            [
                new RelationTuple("folder", "f2", "owner", "group", "g1", "member"),
                new RelationTuple("group", "g1", "member", "user", "join-hit"),
            ]);

            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);

            var counter = new RoundTripCounter();
            services.AddSingleton(counter);
            services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
            {
                var real = ActivatorUtilities.CreateInstance<MySqlDataReaderProvider>(sp);
                return new CountingReaderProvider(real, sp.GetRequiredService<RoundTripCounter>());
            }));

            await using var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

            var result = (await engine.Check(
                new CheckRequest("folder", "f2", "owner", "user", "join-hit", snapToken: snapToken), default)).Allowed;

            result.Should().BeTrue();
            // Without this fast path, this shape costs at least 2 round trips (HasDirectRelation,
            // then GetIndirectRelations plus one recursive fan-out check per matched group). The
            // userset join collapses it to exactly 1, and the request supplies its own snapToken, so
            // no GetLatestSnapToken round trip runs either.
            counter.Count.Should().Be(1);
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class UsersetJoinRelationSpecs(MySqlFixture fixture) : UsersetJoinRelationSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class UsersetJoinRelationSpecs(MariaDbFixture fixture) : UsersetJoinRelationSpecsBase(fixture);
}
