using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
}
