using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class BoolTaskCombinatorSpecs
{
    [Fact]
    public async Task Union_first_true_decides_without_waiting_for_siblings()
    {
        var pending = new TaskCompletionSource<bool>();
        var tasks = new[] { Task.FromResult(true), pending.Task };

        var result = BoolTaskCombinator.AnyOrAll(tasks, 2, shortCircuitOn: true);

        Assert.True(result.IsCompletedSuccessfully);
        Assert.True(await result);
        pending.TrySetResult(false); // avoid dangling task in test host
    }

    [Fact]
    public async Task Union_all_false_returns_false()
    {
        var tasks = new[] { Task.FromResult(false), Task.FromResult(false), Task.FromResult(false) };
        Assert.False(await BoolTaskCombinator.AnyOrAll(tasks, 3, shortCircuitOn: true));
    }

    [Fact]
    public async Task Intersect_first_false_decides_early()
    {
        var pending = new TaskCompletionSource<bool>();
        var tasks = new[] { pending.Task, Task.FromResult(false) };

        var result = BoolTaskCombinator.AnyOrAll(tasks, 2, shortCircuitOn: false);

        Assert.True(result.IsCompletedSuccessfully);
        Assert.False(await result);
        pending.TrySetResult(true);
    }

    [Fact]
    public async Task Intersect_all_true_returns_true()
    {
        var tasks = new[] { Task.FromResult(true), Task.FromResult(true) };
        Assert.True(await BoolTaskCombinator.AnyOrAll(tasks, 2, shortCircuitOn: false));
    }

    [Fact]
    public async Task Late_completion_decides_asynchronously()
    {
        var a = new TaskCompletionSource<bool>();
        var b = new TaskCompletionSource<bool>();
        var result = BoolTaskCombinator.AnyOrAll(new[] { a.Task, b.Task }, 2, shortCircuitOn: true);

        Assert.False(result.IsCompleted);
        a.SetResult(false);
        Assert.False(result.IsCompleted);
        b.SetResult(true);
        Assert.True(await result);
    }

    [Fact]
    public async Task Fault_before_decision_propagates()
    {
        var boom = new InvalidOperationException("boom");
        var tasks = new[] { Task.FromResult(false), Task.FromException<bool>(boom) };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BoolTaskCombinator.AnyOrAll(tasks, 2, shortCircuitOn: true));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Fault_after_decision_is_swallowed_and_observed()
    {
        var pending = new TaskCompletionSource<bool>();
        var tasks = new[] { Task.FromResult(true), pending.Task };
        var result = BoolTaskCombinator.AnyOrAll(tasks, 2, shortCircuitOn: true);
        Assert.True(await result);

        pending.SetException(new InvalidOperationException("late"));
        // Give the continuation a beat to observe the exception (prevents UnobservedTaskException).
        await Task.Delay(50);
        Assert.True(await result);
    }

    [Fact]
    public async Task Only_first_count_tasks_participate()
    {
        // Rented arrays are longer than count — trailing slots must be ignored.
        var tasks = new[] { Task.FromResult(false), Task.FromResult(false), Task.FromResult(true) };
        Assert.False(await BoolTaskCombinator.AnyOrAll(tasks, 2, shortCircuitOn: true));
    }

    [Fact]
    public async Task Empty_union_is_false()
    {
        Assert.False(await BoolTaskCombinator.AnyOrAll(Array.Empty<Task<bool>>(), 0, shortCircuitOn: true));
    }

    [Fact]
    public async Task Empty_intersect_is_true()
    {
        Assert.True(await BoolTaskCombinator.AnyOrAll(Array.Empty<Task<bool>>(), 0, shortCircuitOn: false));
    }
}
