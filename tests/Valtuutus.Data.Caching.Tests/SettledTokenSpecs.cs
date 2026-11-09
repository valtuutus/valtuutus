using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching.Tests;

/// <summary>
/// An answer is cached under a token only once no write can still commit under that token. The
/// latest token this cache knows is always such a settled token; a request carrying a newer one is
/// answered without the cache.
/// </summary>
public sealed class SettledTokenSpecs : IDisposable
{
    private static readonly SnapToken Older = new("01ARZ3NDEKTSV4RRFFQ69G5FAA");
    private static readonly SnapToken Newer = new("01ARZ3NDEKTSV4RRFFQ69G5FAB");

    private static readonly CheckRequest Request = new("doc", "1", "view", "user", "alice");

    private readonly FusionCache _cache = new(new FusionCacheOptions());

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task An_answer_at_a_token_newer_than_the_latest_known_one_is_not_cached()
    {
        await _cache.SetAsync(Consts.LatestSnapTokenKey, Older);
        var inner = new RecordingCheckEngine(Newer);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Request with { SnapToken = Newer }, default);
        await engine.Check(Request with { SnapToken = Newer }, default);

        inner.Requests.Should().HaveCount(2);
        (await _cache.TryGetAsync<SnapToken>(Consts.LatestSnapTokenKey)).Value.Should().Be(Older);
    }

    [Fact]
    public async Task An_answer_at_or_below_the_latest_known_token_is_cached()
    {
        await _cache.SetAsync(Consts.LatestSnapTokenKey, Newer);
        var inner = new RecordingCheckEngine(Older);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Request with { SnapToken = Older }, default);
        await engine.Check(Request with { SnapToken = Older }, default);

        inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Without_a_known_latest_token_an_answer_at_a_given_token_is_not_cached()
    {
        var inner = new RecordingCheckEngine(Older);
        var engine = new CachedCheckEngine(_cache, inner);

        await engine.Check(Request with { SnapToken = Older }, default);
        await engine.Check(Request with { SnapToken = Older }, default);

        inner.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_committed_write_offers_the_latest_settled_token_instead_of_its_own()
    {
        // A write that took its id earlier is still open, so the provider's latest settled token is
        // older than the one just written.
        var reader = Substitute.For<IDataReaderProvider>();
        reader.GetLatestSnapToken(Arg.Any<CancellationToken>()).Returns(Older);
        var registrations = new ServiceCollection();
        registrations.AddValtuutusCore("""
            entity user {}
            entity doc { relation viewer @user; permission view := viewer; }
            """);
        var builder = registrations.AddValtuutusData().AddCaching();
        var services = new ServiceCollection()
            .AddSingleton<IFusionCache>(_cache)
            .AddSingleton(new ValtuutusCachingOptions())
            .AddSingleton(reader)
            .BuildServiceProvider();

        foreach (var handler in builder.Options.OnDataWritten)
            await handler(services, Newer);

        (await _cache.TryGetAsync<SnapToken>(Consts.LatestSnapTokenKey)).Value.Should().Be(Older);
    }
}
