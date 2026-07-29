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
    /// Advances the fake clock and waits for the resulting tick, retrying the Advance() call (not
    /// just the wait) if the first one doesn't produce a signal. This exists specifically for a
    /// test's FIRST tick: PeriodicTimer computes its own start-of-interval baseline from
    /// TimeProvider.GetUtcNow() at construction time, inside ExecuteAsync. On net11.0 with this
    /// repo's runtime-async=on, BackgroundService.StartAsync can return before ExecuteAsync's async
    /// body has actually constructed `new PeriodicTimer(...)` — unlike a normally-compiled async
    /// method's synchronous-until-first-await prefix. When that happens, the test's first Advance()
    /// fires before the timer's baseline exists; the timer is then constructed from the
    /// already-advanced clock and needs a FULL ADDITIONAL interval past that shifted baseline — no
    /// amount of waiting on that same Advance() will ever produce a tick, only a second Advance()
    /// (now measured from the real, shifted baseline) will. Retrying is safe specifically because
    /// PeriodicTimer coalesces: however many Advance() calls it actually takes before the timer
    /// exists to observe them, the consumer only ever receives ONE tick for it — confirmed via an
    /// isolated repro outside this test project. Once this race is behind us (after the first
    /// tick), a single Advance() reliably produces exactly one tick every time — see
    /// AdvanceAndAwaitTick, used for every tick after the first.
    /// </summary>
    private static async Task AdvanceUntilFirstTick(FakeTimeProvider timeProvider, TimeSpan interval, SemaphoreSlim tickSignal)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            timeProvider.Advance(interval);
            if (await tickSignal.WaitAsync(TimeSpan.FromMilliseconds(50)))
            {
                await SettleAfterTick();
                return;
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "Timed out waiting for the first PeriodicTimer tick after repeatedly advancing the fake clock.");
            }
        }
    }

    /// <summary>
    /// Advances the fake clock by one SweepInterval (deterministically triggering exactly one
    /// PeriodicTimer tick, once the timer's construction race in AdvanceUntilFirstTick's doc comment
    /// is behind us) and waits, with a real timeout as a safety net against a genuinely stuck test
    /// (not a tuned interval the timer itself depends on), for the resulting ExecuteAsync loop
    /// iteration to actually finish running.
    /// </summary>
    private static async Task AdvanceAndAwaitTick(FakeTimeProvider timeProvider, TimeSpan interval, SemaphoreSlim tickSignal)
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

        var timeProvider = new FakeTimeProvider();
        var options = new ValtuutusReaperOptions { SweepInterval = TimeSpan.FromMinutes(1) };
        var hostedService = new TombstoneReaperHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            timeProvider,
            NullLogger<TombstoneReaperHostedService>.Instance);

        await hostedService.StartAsync(default);

        // Deterministically drive exactly 3 ticks — no arbitrary wall-clock margin to tune, just a
        // condition-based retry on the first tick (see AdvanceUntilFirstTick) to absorb the
        // documented net11.0 timer-construction race.
        const int expectedTicks = 3;
        await AdvanceUntilFirstTick(timeProvider, options.SweepInterval, tickSignal);
        for (var i = 1; i < expectedTicks; i++)
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

        var timeProvider = new FakeTimeProvider();
        var options = new ValtuutusReaperOptions { SweepInterval = TimeSpan.FromMinutes(1) };
        var hostedService = new TombstoneReaperHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            timeProvider,
            NullLogger<TombstoneReaperHostedService>.Instance);

        var act = async () =>
        {
            await hostedService.StartAsync(default);

            // Two ticks: the first sweep throws, and this proves the loop survives it and keeps
            // ticking rather than the exception unwinding ExecuteAsync and killing the host.
            await AdvanceUntilFirstTick(timeProvider, options.SweepInterval, tickSignal);
            await AdvanceAndAwaitTick(timeProvider, options.SweepInterval, tickSignal);

            await hostedService.StopAsync(default);
        };

        await act.Should().NotThrowAsync();
    }
}
