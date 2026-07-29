using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Valtuutus.Data;

namespace Valtuutus.Data.BackgroundService;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Registers a BackgroundService that calls ITombstoneReaper.ReapAsync on a PeriodicTimer,
    /// using ValtuutusReaperOptions.SweepInterval. Purely a convenience wrapper — you can call
    /// ITombstoneReaper.ReapAsync yourself from any other trigger instead, with or without this.
    /// Requires AddTombstoneReaper (Valtuutus.Data) to have been called first.
    /// </summary>
    public static IServiceCollection AddTombstoneReaperHostedService(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(ITombstoneReaper)))
        {
            throw new InvalidOperationException(
                "AddTombstoneReaperHostedService requires AddTombstoneReaper to already be registered " +
                "(e.g. services.AddValtuutusData().AddTombstoneReaper()).");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<TombstoneReaperHostedService>();
        return services;
    }
}
