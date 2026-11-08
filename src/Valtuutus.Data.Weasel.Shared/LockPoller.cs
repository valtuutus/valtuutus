namespace Valtuutus.Data.Weasel;

/// <summary>
/// Waits for a cross-node lock by repeating a non-blocking attempt, so cancellation and the
/// timeout end the wait the same way on every provider.
/// </summary>
internal static class LockPoller
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(1);

    public static async Task AcquireAsync(Func<Task<bool>> tryAcquire, object lockId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        var delay = InitialDelay;
        while (!await tryAcquire())
        {
            var remaining = timeout - TimeProvider.System.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"Could not take the Valtuutus schema lock '{lockId}' within {timeout}; another process is holding it.");
            await Task.Delay(delay < remaining ? delay : remaining, cancellationToken);
            delay = delay * 2 < MaxDelay ? delay * 2 : MaxDelay;
        }
    }
}
