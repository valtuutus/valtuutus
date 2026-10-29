using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching.Tests;

public sealed class SettleWindowSpecs : IDisposable
{
    // Longer than the time a result read at a token inside the settle window stays cached.
    private static readonly TimeSpan PastYoungResultExpiry = TimeSpan.FromMilliseconds(1500);

    private static readonly CheckRequest Request = new("doc", "1", "view", "user", "alice");

    private readonly FusionCache _cache = new(new FusionCacheOptions());
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => _cache.Dispose();

    private SnapToken TokenTakenNow() => new(Ulid.NewUlid(_time.GetUtcNow()).ToString());

    private ValtuutusCachingOptions Options() => new() { TimeProvider = _time };

    [Fact]
    public async Task A_result_read_at_a_token_inside_the_settle_window_is_read_again_shortly_after()
    {
        var young = TokenTakenNow();
        var inner = new RecordingCheckEngine(young);
        var engine = new CachedCheckEngine(_cache, inner, Options());

        await engine.Check(Request, default);
        await engine.Check(Request, default);
        await Task.Delay(PastYoungResultExpiry);
        await engine.Check(Request, default);

        inner.Requests.Select(r => r.SnapToken).Should().Equal(null, young);
    }

    [Fact]
    public async Task A_result_read_at_a_token_older_than_the_settle_window_stays_cached()
    {
        var inner = new RecordingCheckEngine(TokenTakenNow());
        var engine = new CachedCheckEngine(_cache, inner, Options());
        _time.Advance(TimeSpan.FromMinutes(1));

        await engine.Check(Request, default);
        await Task.Delay(PastYoungResultExpiry);
        await engine.Check(Request, default);

        inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_token_that_is_not_a_ulid_counts_as_older_than_the_settle_window()
    {
        var inner = new RecordingCheckEngine(new SnapToken("not-a-ulid"));
        var engine = new CachedCheckEngine(_cache, inner, Options());

        await engine.Check(Request, default);
        await Task.Delay(PastYoungResultExpiry);
        await engine.Check(Request, default);

        inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_zero_settle_window_caches_every_result_for_the_full_duration()
    {
        var inner = new RecordingCheckEngine(TokenTakenNow());
        var engine = new CachedCheckEngine(_cache, inner,
            Options() with { SettleWindow = TimeSpan.Zero });

        await engine.Check(Request, default);
        await Task.Delay(PastYoungResultExpiry);
        await engine.Check(Request, default);

        inner.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Every_cached_engine_reads_again_shortly_after_a_result_inside_the_settle_window()
    {
        var young = TokenTakenNow();
        var options = Options();
        var check = new RecordingCheckEngine(young);
        var lookupEntity = new RecordingLookupEntityEngine(young);
        var lookupSubject = new RecordingLookupSubjectEngine(young);
        var cachedCheck = new CachedCheckEngine(_cache, check, options);
        var cachedLookupEntity = new CachedLookupEntityEngine(lookupEntity, _cache, options);
        var cachedLookupSubject = new CachedLookupSubjectEngine(lookupSubject, _cache, options);
        var checkRequest = Request with { SnapToken = young };
        var subjectPermissionRequest = new SubjectPermissionRequest
            { EntityType = "doc", EntityId = "1", SubjectType = "user", SubjectId = "alice", SnapToken = young };
        var lookupEntityRequest = new LookupEntityRequest("doc", "view", "user", "alice") { SnapToken = young };
        var lookupSubjectRequest = new LookupSubjectRequest("doc", "view", "user", "1") { SnapToken = young };

        for (var i = 0; i < 2; i++)
        {
            await cachedCheck.Check(checkRequest, default);
            await cachedCheck.SubjectPermission(subjectPermissionRequest, default);
            await cachedLookupEntity.LookupEntity(lookupEntityRequest, default);
            await cachedLookupSubject.Lookup(lookupSubjectRequest, default);
            if (i == 0) await Task.Delay(PastYoungResultExpiry);
        }

        check.Requests.Should().HaveCount(2);
        check.SubjectPermissionRequests.Should().HaveCount(2);
        lookupEntity.Requests.Should().HaveCount(2);
        lookupSubject.Requests.Should().HaveCount(2);
    }

    [Fact]
    public void AddCaching_reads_token_age_from_the_registered_time_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddValtuutusCore("entity user {}");
        services.AddValtuutusData().AddCaching();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ValtuutusCachingOptions>().TimeProvider.Should().BeSameAs(_time);
    }

    [Fact]
    public void AddCaching_registers_the_configured_settle_window()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore("entity user {}");
        services.AddValtuutusData().AddCaching(options => options.SettleWindow = TimeSpan.FromSeconds(30));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ValtuutusCachingOptions>().SettleWindow.Should().Be(TimeSpan.FromSeconds(30));
    }
}
