using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory.Tests;

public class AddTombstoneReaperSpecs
{
    [Fact]
    public void AddTombstoneReaper_registers_options_with_defaults()
    {
        var services = new ServiceCollection();
        services.AddValtuutusData().AddTombstoneReaper();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<ValtuutusReaperOptions>();

        options.RetentionPeriod.Should().Be(TimeSpan.FromDays(7));
        options.BatchSize.Should().Be(1000);
    }

    [Fact]
    public void AddTombstoneReaper_applies_the_configure_callback()
    {
        var services = new ServiceCollection();
        services.AddValtuutusData().AddTombstoneReaper(o => o.RetentionPeriod = TimeSpan.FromDays(1));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<ValtuutusReaperOptions>();

        options.RetentionPeriod.Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public void AddTombstoneReaper_registers_ITombstoneReaper_as_scoped()
    {
        var services = new ServiceCollection();
        services.AddValtuutusData().AddTombstoneReaper();

        var descriptor = services.Single(d => d.ServiceType == typeof(ITombstoneReaper));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [Fact]
    public async Task AddTombstoneReaper_uses_the_registered_time_provider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var reaperProvider = Substitute.For<ITombstoneReaperProvider>();
        reaperProvider.ReapBatchAsync(Arg.Any<SnapToken>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ReapBatchResult(0, 0, 0, MoreRemaining: false, LockAcquired: true));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(reaperProvider);
        services.AddValtuutusData().AddTombstoneReaper();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ITombstoneReaper>().ReapAsync();

        result.Watermark.Should().Be(SnapTokenWatermark.Compute(time.GetUtcNow(), TimeSpan.FromDays(7)));
    }
}
