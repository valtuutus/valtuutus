using Valtuutus.Core.Observability;

namespace Valtuutus.Data;

public abstract class RateLimiterExecuter : IDisposable
{
    protected readonly SemaphoreSlim Semaphore;

    protected RateLimiterExecuter(ValtuutusDataOptions options)
    {
        Semaphore = new SemaphoreSlim(options.MaxConcurrentQueries, options.MaxConcurrentQueries);
    }

    /// <summary>
    /// Counts the query for workload metrics, then acquires the per-request concurrency slot.
    /// All provider read methods must enter through this instead of Semaphore.WaitAsync directly.
    /// </summary>
    protected Task EnterQuery(CancellationToken cancellationToken)
    {
        ValtuutusMetrics.DbQueries.Add(1);
        return Semaphore.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        Semaphore.Dispose();
    }
}
