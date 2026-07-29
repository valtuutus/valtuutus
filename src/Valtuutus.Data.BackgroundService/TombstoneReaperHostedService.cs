using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Valtuutus.Data;

namespace Valtuutus.Data.BackgroundService;

public sealed class TombstoneReaperHostedService(
    IServiceScopeFactory scopeFactory,
    ValtuutusReaperOptions options,
    TimeProvider timeProvider,
    ILogger<TombstoneReaperHostedService> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SweepInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var reaper = scope.ServiceProvider.GetRequiredService<ITombstoneReaper>();
                var result = await reaper.ReapAsync(stoppingToken);

                logger.LogDebug(
                    "Tombstone reaper sweep completed: {RelationTuplesDeleted} relation tuples, {AttributesDeleted} attributes, {TransactionsDeleted} transactions, {BatchesRun} batches",
                    result.RelationTuplesDeleted, result.AttributesDeleted, result.TransactionsDeleted, result.BatchesRun);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tombstone reaper sweep failed");
            }
        }
    }
}
