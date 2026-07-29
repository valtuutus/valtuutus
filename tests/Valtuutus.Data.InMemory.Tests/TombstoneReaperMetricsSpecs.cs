using System.Diagnostics.Metrics;
using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory.Tests;

public class TombstoneReaperMetricsSpecs
{
    // ReapedRows is a process-global static counter and other test classes in this assembly
    // (TombstoneReaperSpecs, TombstoneReaperOrchestratorSpecs) run concurrently and also feed it,
    // so — same as the established V2Metrics/V2WaveMetrics pattern — we never assert on the raw
    // size of the captured list. Instead each test uses batch counts (5/3/2) that don't collide
    // with any value used elsewhere in this assembly's reaper tests, and filters/counts
    // occurrences of those exact (table, value) pairs.
    private static async Task<List<(string Table, long Value)>> MeasureReap(ReapBatchResult batchResult)
    {
        var measurements = new List<(string Table, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Valtuutus" && inst.Name == "valtuutus.data.reaper.reaped_rows")
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            var table = tags.ToArray().First(t => t.Key == "table").Value as string;
            lock (measurements) measurements.Add((table!, value));
        });
        listener.Start();

        var provider = Substitute.For<ITombstoneReaperProvider>();
        provider.ReapBatchAsync(Arg.Any<SnapToken>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(batchResult);
        var reaper = new TombstoneReaper(provider, new ValtuutusReaperOptions { PauseBetweenBatches = TimeSpan.Zero });

        await reaper.ReapAsync();

        listener.Dispose();
        return measurements;
    }

    [Fact]
    public async Task ReapAsync_emits_one_measurement_per_table_when_lock_acquired()
    {
        var measurements = await MeasureReap(new ReapBatchResult(
            RelationTuplesDeleted: 5,
            AttributesDeleted: 3,
            TransactionsDeleted: 2,
            MoreRemaining: false,
            LockAcquired: true));

        measurements.Count(m => m is ("relation_tuples", 5)).Should().Be(1);
        measurements.Count(m => m is ("attributes", 3)).Should().Be(1);
        measurements.Count(m => m is ("transactions", 2)).Should().Be(1);
    }

    [Fact]
    public async Task ReapAsync_emits_no_measurements_when_the_lock_was_not_acquired()
    {
        // Same non-zero, distinct per-table counts as the happy-path test above, but
        // LockAcquired: false — the outer guard in TombstoneReaper.ReapAsync must suppress the
        // entire metrics-emission block, not just skip accumulating totals. Using non-zero counts
        // here (rather than an all-zero no-op batch) is deliberate: with all-zero counts the
        // per-table `> 0` guards would already suppress emission regardless of whether the outer
        // LockAcquired guard exists, so that wouldn't actually exercise the guard this test targets.
        var measurements = await MeasureReap(new ReapBatchResult(
            RelationTuplesDeleted: 5,
            AttributesDeleted: 3,
            TransactionsDeleted: 2,
            MoreRemaining: false,
            LockAcquired: false));

        measurements.Count(m => m is ("relation_tuples", 5)).Should().Be(0);
        measurements.Count(m => m is ("attributes", 3)).Should().Be(0);
        measurements.Count(m => m is ("transactions", 2)).Should().Be(0);
    }
}
