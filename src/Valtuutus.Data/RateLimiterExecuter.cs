namespace Valtuutus.Data;

public abstract class RateLimiterExecuter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    protected RateLimiterExecuter(ValtuutusDataOptions options)
    {
        _semaphore = new SemaphoreSlim(options.MaxConcurrentQueries, options.MaxConcurrentQueries);
    }
    
    protected async Task<T> ExecuteWithRateLimit<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }
    
    public void Dispose()
    {
        _semaphore.Dispose();
    }
}