using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
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
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => _cache.Dispose();

    private SnapToken TokenTakenNow() => new(Ulid.NewUlid(_time.GetUtcNow()).ToString());

    [Fact]
    public async Task A_result_at_a_settled_token_is_cached_for_the_configured_duration()
    {
        var token = TokenTakenNow();
        _time.Advance(TimeSpan.FromMinutes(1));
        var inner = new RecordingCheckEngine(token);
        var engine = new CachedCheckEngine(_cache, inner,
            new ValtuutusCachingOptions { TimeProvider = _time, Duration = ShortDuration });

        await engine.Check(Request with { SnapToken = token }, default);
        await engine.Check(Request with { SnapToken = token }, default);
        await Task.Delay(PastShortDuration);
        await engine.Check(Request with { SnapToken = token }, default);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_result_inside_the_settle_window_is_not_cached_longer_than_the_configured_duration()
    {
        var token = TokenTakenNow();
        var inner = new RecordingCheckEngine(token);
        var engine = new CachedCheckEngine(_cache, inner,
            new ValtuutusCachingOptions { TimeProvider = _time, Duration = ShortDuration });

        await engine.Check(Request with { SnapToken = token }, default);
        await Task.Delay(PastShortDuration);
        await engine.Check(Request with { SnapToken = token }, default);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_latest_token_is_kept_for_the_configured_duration()
    {
        var inner = new RecordingCheckEngine(TokenTakenNow());
        var engine = new CachedCheckEngine(_cache, inner,
            new ValtuutusCachingOptions { TimeProvider = _time, LatestSnapTokenDuration = ShortDuration });

        await engine.Check(Request, default);
        await Task.Delay(PastShortDuration);
        await engine.Check(Request with { EntityId = "2" }, default);

        inner.Requests.Should().HaveCount(2).And.OnlyContain(r => r.SnapToken == null);
    }
}
