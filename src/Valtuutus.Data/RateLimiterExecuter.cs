namespace Valtuutus.Data;

public abstract class RateLimiterExecuter : IDisposable
{
    protected readonly SemaphoreSlim Semaphore;

    protected RateLimiterExecuter(ValtuutusDataOptions options)
    {
        Semaphore = new SemaphoreSlim(options.MaxConcurrentQueries, options.MaxConcurrentQueries);
    }

    public void Dispose()
    {
        Semaphore.Dispose();
    }
}