using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Observability;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class TracingSpecs
{
    private const string Schema = """
        entity user {}
        entity group {
            relation member @user @group#member;
        }
        entity doc {
            relation owner @user;
            relation viewer @group#member;
            permission edit := owner;
            permission view := viewer;
        }
        """;

    private static async Task<(ServiceProvider Services, SnapToken Written)> Seed()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema).AddInMemory();
        var sp = services.BuildServiceProvider();
        var written = await sp.GetRequiredService<IDataWriterProvider>().Write(
        [
            new RelationTuple("doc", "d1", "owner", "user", "alice"),
            new RelationTuple("doc", "d1", "viewer", "group", "g1", "member"),
            new RelationTuple("group", "g1", "member", "group", "g2", "member"),
            new RelationTuple("group", "g2", "member", "user", "bob"),
        ], [], CancellationToken.None);
        return (sp, written);
    }

    [Fact]
    public async Task Check_span_describes_the_request_and_counts_its_round_trips()
    {
        var (sp, written) = await Seed();
        await using var _ = sp;
        var engine = sp.GetRequiredService<ICheckEngine>();

        using var spans = new SpanCollector();
        await engine.Check(new CheckRequest("doc", "d1", "edit", "user", "alice"), default);

        var check = spans.Named("valtuutus.check").Should().ContainSingle().Subject;
        check.Kind.Should().Be(ActivityKind.Internal);
        check.GetTagItem("valtuutus.entity_type").Should().Be("doc");
        check.GetTagItem("valtuutus.entity_id").Should().Be("d1");
        check.GetTagItem("valtuutus.permission").Should().Be("edit");
        check.GetTagItem("valtuutus.subject_type").Should().Be("user");
        check.GetTagItem("valtuutus.subject_id").Should().Be("alice");
        check.GetTagItem("valtuutus.snap_token.source").Should().Be("latest");
        check.GetTagItem("valtuutus.snap_token").Should().Be(written.Value);
        check.GetTagItem("valtuutus.result").Should().Be(true);

        // InMemory cannot fold the latest-token lookup into a batch, so it costs its own round trip.
        var roundTrips = spans.ChildrenOf(check);
        roundTrips.Should().OnlyContain(a => a.OperationName == "valtuutus.round_trip" && a.Kind == ActivityKind.Client);
        roundTrips.Select(a => ((string[])a.GetTagItem("valtuutus.queries")!).Single())
            .Should().BeEquivalentTo("GetLatestSnapToken", "HasDirectRelation");
        check.GetTagItem("valtuutus.round_trips").Should().Be(roundTrips.Count);
    }

    [Fact]
    public async Task A_request_token_is_used_without_resolving_the_latest_one()
    {
        var (sp, written) = await Seed();
        await using var _ = sp;
        var engine = sp.GetRequiredService<ICheckEngine>();

        using var spans = new SpanCollector();
        await engine.Check(new CheckRequest("doc", "d1", "edit", "user", "bob", snapToken: written), default);

        var check = spans.Named("valtuutus.check").Should().ContainSingle().Subject;
        check.GetTagItem("valtuutus.snap_token.source").Should().Be("request");
        check.GetTagItem("valtuutus.result").Should().Be(false);
        check.GetTagItem("valtuutus.round_trips").Should().Be(1);
    }

    [Fact]
    public async Task SubjectPermission_span_counts_the_permissions_allowed()
    {
        var (sp, written) = await Seed();
        await using var _ = sp;
        var engine = sp.GetRequiredService<ICheckEngine>();

        using var spans = new SpanCollector();
        await engine.SubjectPermission(new SubjectPermissionRequest
        {
            EntityType = "doc", EntityId = "d1", SubjectType = "user", SubjectId = "alice", SnapToken = written
        }, default);

        var span = spans.Named("valtuutus.subject_permission").Should().ContainSingle().Subject;
        span.GetTagItem("valtuutus.result_count").Should().Be(1);
        span.GetTagItem("valtuutus.permission").Should().BeNull("every permission of the entity is evaluated");
    }

    [Fact]
    public async Task Explain_span_carries_the_result()
    {
        var (sp, written) = await Seed();
        await using var _ = sp;
        var engine = sp.GetRequiredService<ICheckEngine>();

        using var spans = new SpanCollector();
        await engine.Explain(new CheckRequest("doc", "d1", "view", "user", "bob", snapToken: written), default);

        var span = spans.Named("valtuutus.explain").Should().ContainSingle().Subject;
        span.GetTagItem("valtuutus.result").Should().Be(true);
    }

    [Fact]
    public async Task LookupEntity_round_trips_carry_the_step_that_ran_them()
    {
        var (sp, written) = await Seed();
        await using var _ = sp;
        var engine = sp.GetRequiredService<ILookupEntityEngine>();

        using var spans = new SpanCollector();
        await engine.LookupEntity(new LookupEntityRequest("doc", "view", "user", "bob") { SnapToken = written }, default);

        var lookup = spans.Named("valtuutus.lookup_entity").Should().ContainSingle().Subject;
        lookup.GetTagItem("valtuutus.subject_id").Should().Be("bob");
        lookup.GetTagItem("valtuutus.result_count").Should().Be(1);
        var steps = (int)lookup.GetTagItem("valtuutus.steps")!;
        steps.Should().BeGreaterThan(1, "bob reaches the doc through two groups");

        var roundTrips = spans.ChildrenOf(lookup);
        lookup.GetTagItem("valtuutus.round_trips").Should().Be(roundTrips.Count);
        roundTrips.Select(a => (int)a.GetTagItem("valtuutus.step")!).Distinct()
            .Should().BeEquivalentTo(Enumerable.Range(1, steps));
    }

    [Fact]
    public async Task LookupSubject_span_counts_the_subjects_found()
    {
        var (sp, written) = await Seed();
        await using var _ = sp;
        var engine = sp.GetRequiredService<ILookupSubjectEngine>();

        using var spans = new SpanCollector();
        await engine.Lookup(new LookupSubjectRequest("doc", "view", "user", "d1") { SnapToken = written }, default);

        var lookup = spans.Named("valtuutus.lookup_subject").Should().ContainSingle().Subject;
        lookup.GetTagItem("valtuutus.entity_id").Should().Be("d1");
        lookup.GetTagItem("valtuutus.result_count").Should().Be(1);
        ((int)lookup.GetTagItem("valtuutus.steps")!).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_failed_call_marks_its_span_as_an_error()
    {
        var (sp, written) = await Seed();
        await using var _ = sp;
        var engine = sp.GetRequiredService<ILookupEntityEngine>();

        using var spans = new SpanCollector();
        var act = () => engine.LookupEntity(new LookupEntityRequest("doc", "view", "user", "bob")
        {
            SnapToken = written,
            Scope = new EntityScope("missing", "user", "bob")
        }, default).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();

        var lookup = spans.Named("valtuutus.lookup_entity").Should().ContainSingle().Subject;
        lookup.Status.Should().Be(ActivityStatusCode.Error);
        lookup.GetTagItem("error.type").Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task Writes_and_deletes_report_the_token_they_committed_at()
    {
        var (sp, _) = await Seed();
        await using var services = sp;
        var writer = sp.GetRequiredService<IDataWriterProvider>();

        using var spans = new SpanCollector();
        var written = await writer.Write([new RelationTuple("doc", "d2", "owner", "user", "alice")], [], default);
        var deleted = await writer.Delete(new DeleteFilter
        {
            Relations = [new DeleteRelationsFilter { EntityType = "doc", EntityId = "d2" }]
        }, default);

        spans.Named("valtuutus.write").Should().ContainSingle()
            .Which.GetTagItem("valtuutus.snap_token").Should().Be(written.Value);
        spans.Named("valtuutus.delete").Should().ContainSingle()
            .Which.GetTagItem("valtuutus.snap_token").Should().Be(deleted.Value);
    }

    [Fact]
    public void A_round_trip_still_running_after_the_answer_is_marked_as_a_straggler()
    {
        using var spans = new SpanCollector();
        var request = ValtuutusTelemetry.StartRequest(TelemetryOperation.Check, requestedSnapToken: null)!;
        ValtuutusTelemetry.StartRoundTrip(null, ["HasDirectRelation"], resolvesSnapToken: false).Dispose();
        var straggler = ValtuutusTelemetry.StartRoundTrip(null, ["GetRelations"], resolvesSnapToken: false);

        request.Answer(snapToken: null);
        request.Dispose();
        straggler.Dispose();

        var check = spans.Named("valtuutus.check").Should().ContainSingle().Subject;
        check.GetTagItem("valtuutus.round_trips").Should().Be(2);
        var roundTrips = spans.ChildrenOf(check);
        roundTrips.Single(a => ((string[])a.GetTagItem("valtuutus.queries")!)[0] == "GetRelations")
            .GetTagItem("valtuutus.straggler").Should().Be(true);
        roundTrips.Single(a => ((string[])a.GetTagItem("valtuutus.queries")!)[0] == "HasDirectRelation")
            .GetTagItem("valtuutus.straggler").Should().BeNull();
    }

    [Fact]
    public void A_round_trip_that_responded_before_the_answer_is_not_a_straggler()
    {
        using var spans = new SpanCollector();
        var request = ValtuutusTelemetry.StartRequest(TelemetryOperation.Check, requestedSnapToken: null)!;
        var roundTrip = ValtuutusTelemetry.StartRoundTrip(null, ["HasDirectRelation"], resolvesSnapToken: false);

        roundTrip.Responded();
        request.Answer(snapToken: null);
        request.Dispose();
        roundTrip.Dispose();

        var check = spans.Named("valtuutus.check").Should().ContainSingle().Subject;
        spans.ChildrenOf(check).Should().ContainSingle()
            .Which.GetTagItem("valtuutus.straggler").Should().BeNull();
    }
}
