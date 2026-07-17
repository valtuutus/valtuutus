namespace Valtuutus.Core.Engines.Check.V2;

internal readonly record struct Completion(int Token, bool Result, object? Payload, Exception? Error);

// Single-consumer mailbox between provider completion callbacks (any thread, possibly
// synchronously inside Submit) and the executor driver loop. The TCS is allocated only
// when the consumer actually has to wait — a fully synchronous request (InMemory) never
// allocates one.
internal sealed class CompletionMailbox
{
    private readonly object _lock = new();
    private readonly Queue<Completion> _queue = new();
    private TaskCompletionSource<bool>? _signal;

    // Safe to call only once the previous ExecuteAsync + DrainStragglersAsync for this instance
    // have both fully finished — pool ownership sequencing guarantees no one is still posting
    // into or awaiting this mailbox at that point (see CheckPlanExecutorPool).
    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _signal = null;
        }
    }

    public void Post(in Completion completion)
    {
        TaskCompletionSource<bool>? toSignal;
        lock (_lock)
        {
            _queue.Enqueue(completion);
            toSignal = _signal;
            _signal = null;
        }
        toSignal?.TrySetResult(true);
    }

    public async ValueTask<Completion> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            TaskCompletionSource<bool> signal;
            lock (_lock)
            {
                if (_queue.Count > 0) return _queue.Dequeue();
                signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _signal = signal;
            }
            await signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
