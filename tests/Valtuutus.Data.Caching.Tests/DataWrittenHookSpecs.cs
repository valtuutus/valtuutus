using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching.Tests;

/// <summary>
/// The OnDataWritten hook AddCaching installs: it publishes the written token as the latest one,
/// unless the cache already knows a newer one, and runs a hook configured before it as well.
/// </summary>
public sealed class DataWrittenHookSpecs : IDisposable
{
    private readonly FusionCache _cache = new(new FusionCacheOptions());

    public void Dispose() => _cache.Dispose();

    private static readonly SnapToken Older = new("01ARZ3NDEKTSV4RRFFQ69G5FAA");
    private static readonly SnapToken Newer = new("01ARZ3NDEKTSV4RRFFQ69G5FAB");

    private const string Schema = @"
        entity user {}
        entity project {
            relation owner @user;
            permission edit := owner;
        }
    ";

    [Fact]
    public async Task The_written_token_becomes_the_latest_token()
    {
        var (builder, provider) = Build();

        await builder.Options.OnDataWritten!(provider, Older);

        (await Latest()).Should().Be(Older);
    }

    [Fact]
    public async Task An_older_written_token_does_not_replace_a_newer_latest_token()
    {
        // Two writes finish out of order: the newer one announces itself first.
        var (builder, provider) = Build();

        await builder.Options.OnDataWritten!(provider, Newer);
        await builder.Options.OnDataWritten!(provider, Older);

        (await Latest()).Should().Be(Newer);
    }

    [Fact]
    public async Task A_hook_configured_before_AddCaching_still_runs()
    {
        var announced = new List<SnapToken>();
        var (builder, provider) = Build(options => options.OnDataWritten = (_, token) =>
        {
            announced.Add(token);
            return Task.CompletedTask;
        });

        await builder.Options.OnDataWritten!(provider, Older);

        announced.Should().Equal(Older);
        (await Latest()).Should().Be(Older);
    }

    private (IValtuutusDataBuilder Builder, IServiceProvider Provider) Build(
        Action<ValtuutusDataOptions>? configureBeforeCaching = null)
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        var builder = services.AddValtuutusData();
        configureBeforeCaching?.Invoke(builder.Options);
        builder.AddCaching();
        return (builder, new CacheOnlyServiceProvider(_cache));
    }

    // The hook resolves only the cache from the provider it is handed.
    private sealed class CacheOnlyServiceProvider(IFusionCache cache) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IFusionCache) ? cache : null;
    }

    private async Task<SnapToken?> Latest()
    {
        var latest = await _cache.TryGetAsync<SnapToken>(Consts.LatestSnapTokenKey);
        return latest.HasValue ? latest.Value : null;
    }
}
