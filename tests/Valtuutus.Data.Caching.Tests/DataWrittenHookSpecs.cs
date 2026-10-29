using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching.Tests;

/// <summary>
/// The OnDataWritten handler AddCaching adds: it publishes the written token as the latest one,
/// unless the cache already knows a newer one, alongside any handler added before or after it.
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
        var builder = Build();

        await Announce(builder, Older);

        (await Latest()).Should().Be(Older);
    }

    [Fact]
    public async Task An_older_written_token_does_not_replace_a_newer_latest_token()
    {
        // Two writes finish out of order: the newer one announces itself first.
        var builder = Build();

        await Announce(builder, Newer);
        await Announce(builder, Older);

        (await Latest()).Should().Be(Newer);
    }

    [Fact]
    public async Task After_concurrent_announcements_a_request_without_a_token_reads_at_the_newest_one()
    {
        // Writes committed at the same time announce together: whichever order the announcements
        // land in, a request without a token reads at the newest one.
        var builder = Build();
        const string crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var tokens = Enumerable.Range(0, 64)
            .Select(i => new SnapToken($"01ARZ3NDEKTSV4RRFFQ69G5F{crockford[i / 32]}{crockford[i % 32]}"))
            .ToArray();

        for (var round = 0; round < 50; round++)
        {
            using var cache = new FusionCache(new FusionCacheOptions());
            using var start = new ManualResetEventSlim();
            var announcements = Enumerable.Reverse(tokens)
                .Select(token => Task.Run(async () =>
                {
                    start.Wait();
                    await Announce(builder, token, cache);
                }))
                .ToArray();
            start.Set();
            await Task.WhenAll(announcements);

            var inner = new ReadAtCheckEngine();
            await new CachedCheckEngine(cache, inner).Check(new CheckRequest("project", "1", "edit", "user", "alice"), default);

            inner.ReadAt.Should().Be(tokens[^1], "round {0} must read at the newest token", round);
        }
    }

    [Fact]
    public async Task The_written_token_is_kept_for_the_configured_latest_token_duration()
    {
        var builder = Build();
        var options = new ValtuutusCachingOptions { LatestSnapTokenDuration = TimeSpan.FromMilliseconds(300) };

        await Announce(builder, Older, options: options);
        await Task.Delay(TimeSpan.FromMilliseconds(700));

        (await Latest()).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_handler_added_before_or_after_AddCaching_runs_too(bool addedBefore)
    {
        var announced = new List<SnapToken>();
        Func<IServiceProvider, SnapToken, Task> handler = (_, token) =>
        {
            announced.Add(token);
            return Task.CompletedTask;
        };
        var builder = Build(addedBefore ? options => options.OnDataWritten.Add(handler) : null);
        if (!addedBefore) builder.Options.OnDataWritten.Add(handler);

        await Announce(builder, Older);

        announced.Should().Equal(Older);
        (await Latest()).Should().Be(Older);
    }

    private static IValtuutusDataBuilder Build(Action<ValtuutusDataOptions>? configureBeforeCaching = null)
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        var builder = services.AddValtuutusData();
        configureBeforeCaching?.Invoke(builder.Options);
        builder.AddCaching();
        return builder;
    }

    // Runs the handlers the way a data provider does once a write commits.
    private async Task Announce(IValtuutusDataBuilder builder, SnapToken token, IFusionCache? cache = null,
        ValtuutusCachingOptions? options = null)
    {
        var services = new CachingServiceProvider(cache ?? _cache, options ?? new ValtuutusCachingOptions());
        foreach (var handler in builder.Options.OnDataWritten)
            await handler(services, token);
    }

    // The handlers resolve only the cache and the caching options from the provider they are handed.
    private sealed class CachingServiceProvider(IFusionCache cache, ValtuutusCachingOptions options) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IFusionCache) ? cache
                : serviceType == typeof(ValtuutusCachingOptions) ? options
                : null;
    }

    // Records the token the cache handed it to read at.
    private sealed class ReadAtCheckEngine : ICheckEngine
    {
        public SnapToken? ReadAt { get; private set; }

        public ValueTask<CheckResult> Check(CheckRequest req, CancellationToken cancellationToken)
        {
            ReadAt = req.SnapToken;
            return new(new CheckResult { Allowed = true, SnapToken = req.SnapToken });
        }

        public ValueTask<SubjectPermissionResult> SubjectPermission(SubjectPermissionRequest req,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private async Task<SnapToken?> Latest()
    {
        var latest = await _cache.TryGetAsync<SnapToken>(Consts.LatestSnapTokenKey);
        return latest.HasValue ? latest.Value : null;
    }
}
