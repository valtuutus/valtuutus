using AwesomeAssertions;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Tests.Shared;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching.Tests;

public sealed class CachedEngineSpecs : IDisposable
{
    private static readonly SnapToken Older = new("01ARZ3NDEKTSV4RRFFQ69G5FAA");
    private static readonly SnapToken Newer = new("01ARZ3NDEKTSV4RRFFQ69G5FAB");

    private static readonly CheckRequest Request = new("doc", "1", "view", "user", "alice");

    private readonly FusionCache _cache = new(new FusionCacheOptions());

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task Without_a_known_token_the_engine_resolves_it_and_the_result_is_cached_under_it()
    {
        var inner = new RecordingCheckEngine(Older);
        var engine = new CachedCheckEngine(_cache, inner);

        var first = await engine.Check(Request, default);
        var second = await engine.Check(Request, default);
        var pinned = await engine.Check(Request with { SnapToken = Older }, default);

        first.Should().Be(new CheckResult { Allowed = true, SnapToken = Older });
        second.Should().Be(first);
        pinned.Should().Be(first);
        inner.Requests.Should().ContainSingle()
            .Which.SnapToken.Should().BeNull("the engine resolves the latest token itself");
    }

    [Fact]
    public async Task Cache_span_reports_whether_the_cache_answered()
    {
        await _cache.SetAsync(Consts.LatestSnapTokenKey, Older);
        var engine = new CachedCheckEngine(_cache, new RecordingCheckEngine(Older));

        using var spans = new SpanCollector();
        await engine.Check(Request, default);
        await engine.Check(Request, default);

        spans.Named("valtuutus.cache").Should().HaveCount(2)
            .And.OnlyContain(a => (string?)a.GetTagItem("valtuutus.operation") == "check");
        spans.Named("valtuutus.cache").Select(a => a.GetTagItem("valtuutus.cache.hit"))
            .Should().Equal(false, true);
    }

    [Fact]
    public async Task A_known_latest_token_is_passed_to_the_engine()
    {
        await _cache.SetAsync(Consts.LatestSnapTokenKey, Older);
        var inner = new RecordingCheckEngine(Older);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Request, default);
        await engine.Check(Request, default);

        inner.Requests.Should().ContainSingle().Which.SnapToken.Should().Be(Older);
    }

    [Fact]
    public async Task A_resolved_token_does_not_replace_a_newer_latest_token()
    {
        // A write lands while the request runs: the cache learns the newer token before the
        // engine reports the older one it read at.
        var inner = new RecordingCheckEngine(Older,
            onCall: () => _cache.SetAsync(Consts.LatestSnapTokenKey, Newer).AsTask());
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Request, default);
        await engine.Check(Request, default);

        inner.Requests.Select(r => r.SnapToken).Should().Equal(null, Newer);
    }

    [Fact]
    public async Task A_latest_token_that_moves_backwards_in_the_cache_does_not_move_reads_backwards()
    {
        // Two offers race and the older one lands last (or another node overwrites the shared
        // entry): requests on this node keep reading at the newest token it has seen.
        await _cache.SetAsync(Consts.LatestSnapTokenKey, Newer);
        var inner = new RecordingCheckEngine(Newer);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Request, default);
        await _cache.SetAsync(Consts.LatestSnapTokenKey, Older);
        await engine.Check(Request with { EntityId = "2" }, default);

        inner.Requests.Select(r => r.SnapToken).Should().Equal(Newer, Newer);
    }

    [Fact]
    public async Task A_result_without_a_token_is_not_cached()
    {
        var inner = new RecordingCheckEngine(snapToken: null);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Request, default);
        await engine.Check(Request, default);

        inner.Requests.Should().HaveCount(2).And.OnlyContain(r => r.SnapToken == null);
    }

    [Fact]
    public async Task SubjectPermission_without_a_known_token_is_cached_under_the_reported_token()
    {
        var inner = new RecordingCheckEngine(Older);
        var engine = new CachedCheckEngine(_cache, inner);
        var request = new SubjectPermissionRequest
            { EntityType = "doc", EntityId = "1", SubjectType = "user", SubjectId = "alice" };

        await engine.SubjectPermission(request, default);
        var second = await engine.SubjectPermission(request, default);

        second.SnapToken.Should().Be(Older);
        inner.SubjectPermissionRequests.Should().ContainSingle().Which.SnapToken.Should().BeNull();
    }

    [Fact]
    public async Task LookupEntity_without_a_known_token_is_cached_under_the_reported_token()
    {
        var inner = new RecordingLookupEntityEngine(Older);
        var engine = new CachedLookupEntityEngine(inner, _cache);
        var request = new LookupEntityRequest("doc", "view", "user", "alice");

        await engine.LookupEntity(request, default);
        var second = await engine.LookupEntity(request, default);

        second.SnapToken.Should().Be(Older);
        inner.Requests.Should().ContainSingle().Which.SnapToken.Should().BeNull();
    }

    [Fact]
    public async Task LookupSubject_without_a_known_token_is_cached_under_the_reported_token()
    {
        var inner = new RecordingLookupSubjectEngine(Older);
        var engine = new CachedLookupSubjectEngine(inner, _cache);
        var request = new LookupSubjectRequest("doc", "view", "user", "1");

        await engine.Lookup(request, default);
        var second = await engine.Lookup(request, default);

        second.SnapToken.Should().Be(Older);
        inner.Requests.Should().ContainSingle().Which.SnapToken.Should().BeNull();
    }
}
