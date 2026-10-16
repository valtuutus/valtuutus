using Valtuutus.Core.Observability;

namespace Valtuutus.Data;

/// <summary>
/// Limits how many queries one provider instance runs at once. The semaphore is never disposed: a
/// check answers as soon as its result is known while queries of branches it no longer needs keep
/// running on the same provider, and those can outlive the request scope that owns it. A
/// SemaphoreSlim whose wait handle is never used holds nothing that needs disposing.
/// </summary>
public abstract class RateLimiterExecuter
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
}
