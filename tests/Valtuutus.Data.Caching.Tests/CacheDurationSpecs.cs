using AwesomeAssertions;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching.Tests;

public sealed class CacheDurationSpecs : IDisposable
{
    private static readonly TimeSpan ShortDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PastShortDuration = TimeSpan.FromMilliseconds(700);

    private static readonly CheckRequest Request = new("doc", "1", "view", "user", "alice");

    private readonly FusionCache _cache = new(new FusionCacheOptions());

    public void Dispose() => _cache.Dispose();

    private static SnapToken NewToken() => new(Ulid.NewUlid().ToString());

    [Fact]
    public async Task A_result_is_cached_for_the_configured_duration()
    {
        var token = NewToken();
        await _cache.SetAsync(Consts.LatestSnapTokenKey, token);
        var inner = new RecordingCheckEngine(token);
        var engine = new CachedCheckEngine(_cache, inner,
            new ValtuutusCachingOptions { Duration = ShortDuration });

        await engine.Check(Request with { SnapToken = token }, default);
        await engine.Check(Request with { SnapToken = token }, default);
        await Task.Delay(PastShortDuration);
        await engine.Check(Request with { SnapToken = token }, default);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_latest_token_is_kept_for_the_configured_duration()
    {
        var inner = new RecordingCheckEngine(NewToken());
        var engine = new CachedCheckEngine(_cache, inner,
            new ValtuutusCachingOptions { LatestSnapTokenDuration = ShortDuration });

        await engine.Check(Request, default);
        await Task.Delay(PastShortDuration);
        await engine.Check(Request with { EntityId = "2" }, default);

        inner.Requests.Should().HaveCount(2).And.OnlyContain(r => r.SnapToken == null);
    }
}
