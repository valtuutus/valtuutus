using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Valtuutus.Data;
using Xunit;

namespace Valtuutus.Data.BackgroundService.Tests;

public class DependencyInjectionExtensionsSpecs
{
    [Fact]
    public void AddTombstoneReaperHostedService_throws_when_AddTombstoneReaper_was_not_called()
    {
        var services = new ServiceCollection();
        services.AddValtuutusData();

        var act = () => services.AddTombstoneReaperHostedService();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddTombstoneReaper*");
    }

    [Fact]
    public void AddTombstoneReaperHostedService_registers_a_hosted_service_when_reaper_is_present()
    {
        var services = new ServiceCollection();
        services.AddValtuutusData().AddTombstoneReaper();

        services.AddTombstoneReaperHostedService();

        services.Should().Contain(d => d.ServiceType == typeof(IHostedService));
    }
}
