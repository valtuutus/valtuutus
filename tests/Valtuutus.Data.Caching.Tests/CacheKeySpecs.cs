using AwesomeAssertions;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching.Tests;

public sealed class CacheKeySpecs : IDisposable
{
    private static readonly SnapToken Token = new("01ARZ3NDEKTSV4RRFFQ69G5FAA");

    private static readonly CheckRequest Check = new("household", "1", "remove_member", "user", "ana")
        { SnapToken = Token };

    private readonly FusionCache _cache = new(new FusionCacheOptions());

    // The requests' token is known to be settled, so their answers are cached.
    public CacheKeySpecs() => _cache.Set(Consts.LatestSnapTokenKey, Token);

    public void Dispose() => _cache.Dispose();

    private static Dictionary<string, object> Ctx(params (string Key, object Value)[] entries)
        => entries.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public async Task Checks_with_different_context_do_not_share_a_cache_entry()
    {
        var inner = new RecordingCheckEngine(Token);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Check with { Context = Ctx(("target_role", "member")) }, default);
        await engine.Check(Check with { Context = Ctx(("target_role", "owner")) }, default);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Context_values_of_different_types_do_not_share_a_cache_entry()
    {
        var inner = new RecordingCheckEngine(Token);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Check with { Context = Ctx(("n", 1)) }, default);
        await engine.Check(Check with { Context = Ctx(("n", 1m)) }, default);
        await engine.Check(Check with { Context = Ctx(("n", "1")) }, default);

        inner.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Equal_context_in_a_different_order_shares_a_cache_entry()
    {
        var inner = new RecordingCheckEngine(Token);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Check with { Context = Ctx(("a", "x"), ("b", true)) }, default);
        await engine.Check(Check with { Context = Ctx(("b", true), ("a", "x")) }, default);

        inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_context_value_of_an_unsupported_type_bypasses_the_cache()
    {
        var inner = new RecordingCheckEngine(Token);
        var engine = new CachedCheckEngine(_cache, inner);
        var request = Check with { Context = Ctx(("at", new DateTime(2026, 11, 3))) };

        await engine.Check(request, default);
        await engine.Check(request, default);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Checks_with_different_depth_do_not_share_a_cache_entry()
    {
        var inner = new RecordingCheckEngine(Token);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Check with { Depth = 1 }, default);
        await engine.Check(Check with { Depth = 10 }, default);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task SubjectPermission_with_different_context_does_not_share_a_cache_entry()
    {
        var inner = new RecordingCheckEngine(Token);
        var engine = new CachedCheckEngine(_cache, inner);
        var request = new SubjectPermissionRequest
            { EntityType = "household", EntityId = "1", SubjectType = "user", SubjectId = "ana", SnapToken = Token };

        await engine.SubjectPermission(request with { Context = Ctx(("target_role", "member")) }, default);
        await engine.SubjectPermission(request with { Context = Ctx(("target_role", "owner")) }, default);

        inner.SubjectPermissionRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task LookupEntity_with_different_context_does_not_share_a_cache_entry()
    {
        var inner = new RecordingLookupEntityEngine(Token);
        var engine = new CachedLookupEntityEngine(inner, _cache);
        var request = new LookupEntityRequest("household", "remove_member", "user", "ana") { SnapToken = Token };

        await engine.LookupEntity(request with { Context = Ctx(("target_role", "member")) }, default);
        await engine.LookupEntity(request with { Context = Ctx(("target_role", "owner")) }, default);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task LookupSubject_with_different_context_does_not_share_a_cache_entry()
    {
        var inner = new RecordingLookupSubjectEngine(Token);
        var engine = new CachedLookupSubjectEngine(inner, _cache);
        var request = new LookupSubjectRequest("household", "remove_member", "user", "1") { SnapToken = Token };

        await engine.Lookup(request with { Context = Ctx(("target_role", "member")) }, default);
        await engine.Lookup(request with { Context = Ctx(("target_role", "owner")) }, default);

        inner.Requests.Should().HaveCount(2);
    }
}
