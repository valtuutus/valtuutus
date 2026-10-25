using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;
using Xunit.Abstractions;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// The full-fusion pass collapses a heterogeneous multi-leaf boolean combination into exactly
    /// one physical round trip, for three shapes: an OR of a direct relation and a
    /// tuple-to-userset fast-path ref, an AND of a fast-path ref with a nested OR of direct
    /// relations, and an AND chain with a negated sibling.
    ///
    /// A fused op is a single op whether or not IRelationalBatchOps is registered, since fusion
    /// already reduces the wave to one op at plan-rewrite time. These specs register no batch ops,
    /// so HasFusedExpression lands on the counted reader as exactly one call.
    /// </summary>
    public abstract class FusedExpressionRoundTripSpecsBase(MySqlFixtureBase fixture, ITestOutputHelper output)
        : IAsyncLifetime
    {
        private const string Schema = """
            entity user {}
            entity organization { relation admin @user; }
            entity team {
                relation owner @user;
                relation member @user;
                relation banned @user;
                relation org @organization;
                permission edit := org.admin or owner;
                permission invite := org.admin and (owner or member);
                permission negate_sibling_batch := owner and member and not(banned);
            }
            """;

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task OrFusion_NoTuplesAtAll_ReturnsFalse_WithOneRoundTrip()
        {
            // Nobody holds org.admin or owner on team:t1, so both branches must be evaluated: a miss
            // can't short-circuit on the first branch alone.
            var (result, roundTrips) = await RunCheck("team", "t1", "edit", []);

            output.WriteLine($"edit (no grants): result={result}, roundTrips={roundTrips}");

            result.Should().BeFalse();
            roundTrips.Should().Be(1);
        }

        [Fact]
        public async Task AndOrFusion_AdminAndMember_ReturnsTrue_WithOneRoundTrip()
        {
            // org.admin and (owner or member): alice is admin of the team's organization and member
            // of the team, so both sides of the AND resolve.
            var (result, roundTrips) = await RunCheck("team", "t1", "invite",
            [
                new RelationTuple("organization", "org1", "admin", "user", "alice"),
                new RelationTuple("team", "t1", "org", "organization", "org1"),
                new RelationTuple("team", "t1", "member", "user", "alice"),
            ]);

            output.WriteLine($"invite (admin+member): result={result}, roundTrips={roundTrips}");

            result.Should().BeTrue();
            roundTrips.Should().Be(1);
        }

        [Fact]
        public async Task AndOrFusion_OwnerWithoutAdmin_ReturnsFalse_WithOneRoundTrip()
        {
            // Owner alone satisfies the nested OR, but org.admin never holds for alice, so the outer
            // AND must still fail.
            var (result, roundTrips) = await RunCheck("team", "t1", "invite",
            [
                new RelationTuple("team", "t1", "owner", "user", "alice"),
            ]);

            output.WriteLine($"invite (owner only): result={result}, roundTrips={roundTrips}");

            result.Should().BeFalse();
            roundTrips.Should().Be(1);
        }

        [Fact]
        public async Task NegationFusion_BannedSubjectExcluded_ReturnsFalse_WithOneRoundTrip()
        {
            // alice is owner and member but also banned; the negated sibling vetoes the whole AND.
            var (result, roundTrips) = await RunCheck("team", "t1", "negate_sibling_batch",
            [
                new RelationTuple("team", "t1", "owner", "user", "alice"),
                new RelationTuple("team", "t1", "member", "user", "alice"),
                new RelationTuple("team", "t1", "banned", "user", "alice"),
            ]);

            output.WriteLine($"negate_sibling_batch (banned): result={result}, roundTrips={roundTrips}");

            result.Should().BeFalse();
            roundTrips.Should().Be(1);
        }

        [Fact]
        public async Task NegationFusion_NotBanned_ReturnsTrue_WithOneRoundTrip()
        {
            // Without the banned tuple the negated sibling holds, so the whole AND holds.
            var (result, roundTrips) = await RunCheck("team", "t1", "negate_sibling_batch",
            [
                new RelationTuple("team", "t1", "owner", "user", "alice"),
                new RelationTuple("team", "t1", "member", "user", "alice"),
            ]);

            output.WriteLine($"negate_sibling_batch (not banned): result={result}, roundTrips={roundTrips}");

            result.Should().BeTrue();
            roundTrips.Should().Be(1);
        }

        private async Task<(bool Result, int RoundTrips)> RunCheck(
            string entityType, string entityId, string permission, RelationTuple[] tuples)
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);

            var counter = new RoundTripCounter();
            services.AddSingleton(counter);

            // No IRelationalBatchOps is registered, so every op lands on this reader.
            services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
            {
                var real = ActivatorUtilities.CreateInstance<MySqlDataReaderProvider>(sp);
                return new CountingReaderProvider(real, sp.GetRequiredService<RoundTripCounter>());
            }));
            services.RemoveAll<IRelationalBatchOps>();

            await using var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();

            // The check carries the write's own token, so it never resolves one through the counted
            // reader. The writer never touches the reader. A freshly reset table has nothing to read,
            // so the no-tuple case can use SnapToken.MinValue without writing.
            var snapToken = SnapToken.MinValue;
            if (tuples.Length > 0)
            {
                var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
                snapToken = await writer.Write(tuples, [], default);
            }

            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
            var result = (await engine.Check(
                new CheckRequest(entityType, entityId, permission, "user", "alice", snapToken: snapToken), default)).Allowed;
            return (result, counter.Count);
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class FusedExpressionRoundTripSpecs(MySqlFixture fixture, ITestOutputHelper output)
        : FusedExpressionRoundTripSpecsBase(fixture, output);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class FusedExpressionRoundTripSpecs(MariaDbFixture fixture, ITestOutputHelper output)
        : FusedExpressionRoundTripSpecsBase(fixture, output);
}
