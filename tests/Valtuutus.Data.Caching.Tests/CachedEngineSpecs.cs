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

    // Answers at the given token when the request carries none, and at the request's token otherwise.
    private sealed class RecordingCheckEngine(SnapToken? snapToken, Func<Task>? onCall = null) : ICheckEngine
    {
        public List<CheckRequest> Requests { get; } = [];
        public List<SubjectPermissionRequest> SubjectPermissionRequests { get; } = [];

        public async ValueTask<CheckResult> Check(CheckRequest req, CancellationToken cancellationToken)
        {
            Requests.Add(req);
            if (onCall is not null) await onCall();
            return new CheckResult { Allowed = true, SnapToken = req.SnapToken ?? snapToken };
        }

        public ValueTask<SubjectPermissionResult> SubjectPermission(SubjectPermissionRequest req,
            CancellationToken cancellationToken)
        {
            SubjectPermissionRequests.Add(req);
            return new(new SubjectPermissionResult
                { Permissions = new() { ["view"] = true }, SnapToken = req.SnapToken ?? snapToken });
        }

        public ValueTask<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class RecordingLookupEntityEngine(SnapToken snapToken) : ILookupEntityEngine
    {
        public List<LookupEntityRequest> Requests { get; } = [];

        public ValueTask<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
        {
            Requests.Add(req);
            return new(new LookupEntityPage(["1"], null) { SnapToken = req.SnapToken ?? snapToken });
        }
    }

    private sealed class RecordingLookupSubjectEngine(SnapToken snapToken) : ILookupSubjectEngine
    {
        public List<LookupSubjectRequest> Requests { get; } = [];

        public ValueTask<LookupSubjectResult> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken)
        {
            Requests.Add(req);
            return new(new LookupSubjectResult { SubjectIds = ["alice"], SnapToken = req.SnapToken ?? snapToken });
        }
    }
}
