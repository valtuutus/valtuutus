using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

/// <summary>
/// Real-SqlServer proof that RelationalPlanRewriter's full-fusion pass collapses a genuinely
/// heterogeneous multi-leaf boolean combination into exactly one physical round trip, for the
/// three shapes distinguished in RelationalPlanRewriterSpecs: an OR of a direct relation and a
/// tuple-to-userset fast-path ref, an AND of a fast-path ref with a nested OR of direct
/// relations, and an AND chain with a negated sibling.
///
/// A fused `PhysicalCheckNode(FusedExpressionOp)` is a single op regardless of whether
/// IRelationalBatchOps is registered — DbBatch packing only matters once a wave has more than
/// one op to pack, and fusion here already reduces the wave to one op at plan-rewrite time. So
/// these tests don't register IRelationalBatchOps at all and rely on the simpler
/// individual-dispatch path (the same one BatchedExecutorRoundTripSpecs exercises with
/// batching: false) — HasFusedExpression still lands on the counted reader below as exactly one
/// call either way, with no need to also stand up a counting batch decorator.
/// </summary>
[Collection("SqlServerSpec")]
public sealed class FusedExpressionRoundTripSpecs : IAsyncLifetime
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

    public FusedExpressionRoundTripSpecs(SqlServerFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        Fixture = fixture;
        Output = output;
    }

    private SqlServerFixture Fixture { get; }
    private Xunit.Abstractions.ITestOutputHelper Output { get; }

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OrFusion_NoTuplesAtAll_ReturnsFalse_WithOneRoundTrip()
    {
        // Nobody holds org.admin or owner on team:t1 — both the TTU fast-path branch and the
        // direct-relation branch must be evaluated (a miss can't short-circuit on the first
        // branch alone), which is exactly the shape that used to cost one round trip per branch
        // before fusion collapsed the whole union into one FusedExpressionOp.
        var (result, roundTrips) = await RunCheck("team", "t1", "edit", []);

        Output.WriteLine($"edit (no grants): result={result}, roundTrips={roundTrips}");

        result.Should().BeFalse();
        roundTrips.Should().Be(1);
    }

    [Fact]
    public async Task AndOrFusion_AdminAndMember_ReturnsTrue_WithOneRoundTrip()
    {
        // org.admin and (owner or member): alice holds admin on the team's organization and
        // member (not owner) on the team itself — the AND requires both sides to actually
        // resolve, and the nested OR requires at least one of its two direct relations to hold.
        var (result, roundTrips) = await RunCheck("team", "t1", "invite",
        [
            new RelationTuple("organization", "org1", "admin", "user", "alice"),
            new RelationTuple("team", "t1", "org", "organization", "org1"),
            new RelationTuple("team", "t1", "member", "user", "alice"),
        ]);

        Output.WriteLine($"invite (admin+member): result={result}, roundTrips={roundTrips}");

        result.Should().BeTrue();
        roundTrips.Should().Be(1);
    }

    [Fact]
    public async Task AndOrFusion_OwnerWithoutAdmin_ReturnsFalse_WithOneRoundTrip()
    {
        // Owner alone satisfies the nested (owner or member) OR, but org.admin never holds for
        // alice, so the outer AND must still fail — proving the fused SQL actually enforces the
        // AND rather than only ever reporting the OR's answer.
        var (result, roundTrips) = await RunCheck("team", "t1", "invite",
        [
            new RelationTuple("team", "t1", "owner", "user", "alice"),
        ]);

        Output.WriteLine($"invite (owner only): result={result}, roundTrips={roundTrips}");

        result.Should().BeFalse();
        roundTrips.Should().Be(1);
    }

    [Fact]
    public async Task NegationFusion_BannedSubjectExcluded_ReturnsFalse_WithOneRoundTrip()
    {
        // owner and member and not(banned): alice holds owner and member but is ALSO banned —
        // the negated sibling must still veto the whole AND despite the other two branches
        // holding.
        var (result, roundTrips) = await RunCheck("team", "t1", "negate_sibling_batch",
        [
            new RelationTuple("team", "t1", "owner", "user", "alice"),
            new RelationTuple("team", "t1", "member", "user", "alice"),
            new RelationTuple("team", "t1", "banned", "user", "alice"),
        ]);

        Output.WriteLine($"negate_sibling_batch (banned): result={result}, roundTrips={roundTrips}");

        result.Should().BeFalse();
        roundTrips.Should().Be(1);
    }

    [Fact]
    public async Task NegationFusion_NotBanned_ReturnsTrue_WithOneRoundTrip()
    {
        // Same owner+member grant, without the banned tuple — the negated sibling now resolves
        // to "not banned" == true, so the whole AND holds.
        var (result, roundTrips) = await RunCheck("team", "t1", "negate_sibling_batch",
        [
            new RelationTuple("team", "t1", "owner", "user", "alice"),
            new RelationTuple("team", "t1", "member", "user", "alice"),
        ]);

        Output.WriteLine($"negate_sibling_batch (not banned): result={result}, roundTrips={roundTrips}");

        result.Should().BeTrue();
        roundTrips.Should().Be(1);
    }

    private async Task<(bool Result, int RoundTrips)> RunCheck(
        string entityType, string entityId, string permission, RelationTuple[] tuples)
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;

        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddSqlServer(_ => dbFactory).AddConcurrentQueryLimit(3);
        services.AddValtuutusCheckV2();

        var counter = new RoundTripCounter();
        services.AddSingleton(counter);

        // Same counting decorator BatchedExecutorRoundTripSpecs uses for the individual-dispatch
        // run — no IRelationalBatchOps is registered here at all, so every op (fused or not)
        // lands on this reader.
        services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
        {
            var real = ActivatorUtilities.CreateInstance<SqlServerDataReaderProvider>(sp);
            return new CountingReaderProvider(real, sp.GetRequiredService<RoundTripCounter>());
        }));
        services.RemoveAll<IRelationalBatchOps>();

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();

        // The write's own return value carries a SnapToken for the data it just committed, so
        // passing it explicitly on the CheckRequest below avoids a separate GetLatestSnapToken
        // call — that call goes through the same counted reader as HasFusedExpression, and would
        // otherwise inflate the round-trip count by one regardless of fusion. Writer.Write itself
        // never touches IDataReaderProvider, so it's never counted here either way. An empty
        // table (freshly reset per test) has nothing to miss regardless of which snapshot bound
        // is used, so the no-tuple scenario can use SnapToken.MinValue without writing anything.
        var snapToken = SnapToken.MinValue;
        if (tuples.Length > 0)
        {
            var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
            snapToken = await writer.Write(tuples, [], default);
        }

        var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
        var result = await engine.Check(
            new CheckRequest(entityType, entityId, permission, "user", "alice", snapToken: snapToken), default);
        return (result, counter.Count);
    }
}
