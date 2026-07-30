using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Valtuutus.Data;
using Xunit;

namespace Valtuutus.Data.BackgroundService.Tests;

public class TombstoneReaperHostedServiceSpecs
{
    /// <summary>
    /// A scoped marker with no behavior of its own — its only purpose is to let the test observe
    /// scope identity from the outside. Because it is registered Scoped, the DI container hands
    /// back a new instance every time a new scope resolves it, and the same instance for any
    /// resolution within one scope.
    /// </summary>
    private sealed class ScopeMarker;

    /// <summary>
    /// A FakeTimeProvider that resolves <see cref="TimerCreated"/> the instant a PeriodicTimer
    /// backed by it exists: PeriodicTimer's constructor calls CreateTimer synchronously as its
    /// first action, so overriding it gives a deterministic signal for "ExecuteAsync has reached
    /// `new PeriodicTimer(...)`" — no need to guess via clock-advance-and-poll. This matters because
    /// on net11.0 with this repo's runtime-async=on, BackgroundService.StartAsync can return before
    /// ExecuteAsync's async body has actually constructed the timer, unlike a normally-compiled
    /// async method's synchronous-until-first-await prefix; advancing the clock before that point is
    /// invisible to the timer once it does get constructed.
    /// </summary>
    private sealed class SignalingTimeProvider : FakeTimeProvider
    {
        private readonly TaskCompletionSource _timerCreated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _timerCreated.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timerCreated.TrySetResult();
            return timer;
        }
    }

    /// <summary>
    /// Advances the fake clock by one SweepInterval (deterministically triggering exactly one
    /// PeriodicTimer tick, once SignalingTimeProvider's TimerCreated has resolved) and waits, with a
    /// real timeout as a safety net against a genuinely stuck test (not a tuned interval the timer
    /// itself depends on), for the resulting ExecuteAsync loop iteration to actually finish running.
    /// </summary>
    private static async Task AdvanceAndAwaitTick(SignalingTimeProvider timeProvider, TimeSpan interval, SemaphoreSlim tickSignal)
    {
        timeProvider.Advance(interval);
        var signaled = await tickSignal.WaitAsync(TimeSpan.FromSeconds(5));
        signaled.Should().BeTrue("advancing virtual time by one SweepInterval should promptly trigger a tick");
        await SettleAfterTick();
    }

    /// <summary>
    /// PeriodicTimer never queues more than one pending tick: after the mocked ReapAsync call
    /// signals, the loop still has to run its (synchronous) catch/log handling and get back to
    /// awaiting timer.WaitForNextTickAsync before it is re-armed for the next period. A short fixed
    /// settle delay here avoids racing the very next Advance() call ahead of that re-arm and having
    /// its tick silently coalesced away.
    /// </summary>
    private static Task SettleAfterTick() => Task.Delay(TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task ExecuteAsync_ticks_repeatedly_and_resolves_a_fresh_scope_each_tick()
    {
        var reapCallCount = 0;
        var tickSignal = new SemaphoreSlim(0);
        var reaper = Substitute.For<ITombstoneReaper>();
        reaper.ReapAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref reapCallCount);
            tickSignal.Release();
            return Task.FromResult(new ReapResult(0, 0, 0, 0, null));
        });

        var observedMarkers = new List<ScopeMarker>();
        var services = new ServiceCollection();
        services.AddScoped<ScopeMarker>();
        services.AddScoped<ITombstoneReaper>(sp =>
        {
            // Resolving ITombstoneReaper is the one action ExecuteAsync performs against the
            // scope it creates each iteration. Capturing the sibling ScopeMarker at that exact
            // moment lets us tell scopes apart from outside the private scope ExecuteAsync owns.
            observedMarkers.Add(sp.GetRequiredService<ScopeMarker>());
            return reaper;
        });
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var timeProvider = new SignalingTimeProvider();
        var options = new ValtuutusReaperOptions { SweepInterval = TimeSpan.FromMinutes(1) };
        var hostedService = new TombstoneReaperHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            timeProvider,
            NullLogger<TombstoneReaperHostedService>.Instance);

        await hostedService.StartAsync(default);
        await timeProvider.TimerCreated;

        // Deterministically drive exactly 3 ticks — no arbitrary wall-clock margin to tune, just the
        // TimerCreated await above to clear the documented net11.0 timer-construction race.
        const int expectedTicks = 3;
        for (var i = 0; i < expectedTicks; i++)
        {
            await AdvanceAndAwaitTick(timeProvider, options.SweepInterval, tickSignal);
        }

        await hostedService.StopAsync(default);

        // Proves real periodic ticking, not a single fluke call — with the fake clock we get an
        // exact count rather than merely "at least one".
        reapCallCount.Should().Be(expectedTicks);

        // Proves each tick resolved ITombstoneReaper from its own fresh scope rather than a scope
        // hoisted above the loop and reused: a hoisted scope would only ever resolve
        // ITombstoneReaper once (before the loop starts), leaving observedMarkers with a single
        // entry no matter how many ticks fired.
        observedMarkers.Should().HaveCount(expectedTicks);
        observedMarkers.Distinct().Count().Should().Be(observedMarkers.Count,
            "each tick must create its own scope, so every observed marker should be a distinct instance");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_throw_the_host_down_when_a_sweep_fails()
    {
        var tickSignal = new SemaphoreSlim(0);
        var reaper = Substitute.For<ITombstoneReaper>();
        reaper.ReapAsync(Arg.Any<CancellationToken>()).Returns<Task<ReapResult>>(_ =>
        {
            tickSignal.Release();
            throw new InvalidOperationException("boom");
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => reaper);
        await using var provider = services.BuildServiceProvider();

        var timeProvider = new SignalingTimeProvider();
        var options = new ValtuutusReaperOptions { SweepInterval = TimeSpan.FromMinutes(1) };
        var hostedService = new TombstoneReaperHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            timeProvider,
            NullLogger<TombstoneReaperHostedService>.Instance);

        var act = async () =>
        {
            await hostedService.StartAsync(default);
            await timeProvider.TimerCreated;

            // Two ticks: the first sweep throws, and this proves the loop survives it and keeps
            // ticking rather than the exception unwinding ExecuteAsync and killing the host.
            await AdvanceAndAwaitTick(timeProvider, options.SweepInterval, tickSignal);
            await AdvanceAndAwaitTick(timeProvider, options.SweepInterval, tickSignal);

            await hostedService.StopAsync(default);
        };

        await act.Should().NotThrowAsync();
    }
}
