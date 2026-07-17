using Valtuutus.Core.Observability;

namespace Valtuutus.Core.Engines.Check;

/// <summary>
/// Short-circuiting boolean join over a set of tasks, replacing the previous
/// CTS + ContinueWith + Task.WhenAll + OperationCanceledException pattern.
/// Completes with <c>shortCircuitOn</c> as soon as any task yields it; otherwise completes
/// with the opposite value after all tasks finish. A faulted child propagates its exception
/// unless the result was already decided (matching the old behavior of abandoning siblings
/// after a decision — undecided siblings keep running; they are memoized and harmless).
/// </summary>
internal static class BoolTaskCombinator
{
    /// <param name="tasks">Backing array (may be longer than <paramref name="count"/> — pooled).</param>
    /// <param name="count">Number of leading slots that participate.</param>
    /// <param name="shortCircuitOn">true for Union (any true wins), false for Intersect (any false wins).</param>
    public static Task<bool> AnyOrAll(Task<bool>[] tasks, int count, bool shortCircuitOn)
    {
        if (count == 0) return Task.FromResult(!shortCircuitOn);

        var state = new State(count, shortCircuitOn);
        for (var i = 0; i < count; i++)
        {
            var t = tasks[i];
            if (t.IsCompletedSuccessfully)
            {
                state.Report(t.Result);
                if (state.Result.IsCompleted) return state.Result;
            }
            else
            {
                t.ContinueWith(
                    static (t, s) => ((State)s!).OnChildCompleted(t),
                    state,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        return state.Result;
    }

    private sealed class State
    {
        private readonly TaskCompletionSource<bool> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _shortCircuitOn;
        private int _remaining;

        public State(int count, bool shortCircuitOn)
        {
            _remaining = count;
            _shortCircuitOn = shortCircuitOn;
        }

        public Task<bool> Result => _tcs.Task;

        internal void OnChildCompleted(Task<bool> t)
        {
            if (t.IsFaulted)
            {
                // Touch the exception even when the node is already decided, so an abandoned
                // sibling's failure never surfaces as UnobservedTaskException.
                var ex = t.Exception!;
                _tcs.TrySetException(ex.InnerExceptions);
                return;
            }
            if (t.IsCanceled)
            {
                _tcs.TrySetCanceled();
                return;
            }
            Report(t.Result);
        }

        internal void Report(bool result)
        {
            if (result == _shortCircuitOn)
            {
                if (_tcs.TrySetResult(_shortCircuitOn) && Volatile.Read(ref _remaining) > 1)
                    ValtuutusMetrics.ShortCircuits.Add(1);
                Interlocked.Decrement(ref _remaining);
            }
            else if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _tcs.TrySetResult(!_shortCircuitOn);
            }
        }
    }
}
