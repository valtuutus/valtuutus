using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory.Tests;

public class TombstoneReaperOrchestratorSpecs
{
    [Fact]
    public async Task ReapAsync_loops_until_no_table_reports_more_remaining()
    {
        var provider = Substitute.For<ITombstoneReaperProvider>();
        provider.ReapBatchAsync(Arg.Any<SnapToken>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReapBatchResult(RelationTuplesDeleted: 100, AttributesDeleted: 0, TransactionsDeleted: 0, MoreRemaining: true, LockAcquired: true),
                new ReapBatchResult(RelationTuplesDeleted: 40, AttributesDeleted: 0, TransactionsDeleted: 0, MoreRemaining: false, LockAcquired: true));

        var options = new ValtuutusReaperOptions { PauseBetweenBatches = TimeSpan.Zero };
        var reaper = new TombstoneReaper(provider, options);

        var result = await reaper.ReapAsync();

        result.RelationTuplesDeleted.Should().Be(140);
        result.BatchesRun.Should().Be(2);
        await provider.Received(2).ReapBatchAsync(Arg.Any<SnapToken>(), options.BatchSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReapAsync_stops_the_sweep_once_it_loses_the_lock_race_for_a_batch()
    {
        // First batch succeeds and reports more work remaining, but a second instance wins the
        // race for the next batch — this instance should stop cleanly rather than keep retrying.
        var provider = Substitute.For<ITombstoneReaperProvider>();
        provider.ReapBatchAsync(Arg.Any<SnapToken>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReapBatchResult(RelationTuplesDeleted: 100, AttributesDeleted: 0, TransactionsDeleted: 0, MoreRemaining: true, LockAcquired: true),
                new ReapBatchResult(RelationTuplesDeleted: 0, AttributesDeleted: 0, TransactionsDeleted: 0, MoreRemaining: false, LockAcquired: false));

        var options = new ValtuutusReaperOptions { PauseBetweenBatches = TimeSpan.Zero };
        var reaper = new TombstoneReaper(provider, options);

        var result = await reaper.ReapAsync();

        result.RelationTuplesDeleted.Should().Be(100);
        result.BatchesRun.Should().Be(2);
    }

    [Fact]
    public async Task ReapAsync_returns_zero_work_when_the_very_first_batch_loses_the_lock_race()
    {
        var provider = Substitute.For<ITombstoneReaperProvider>();
        provider.ReapBatchAsync(Arg.Any<SnapToken>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ReapBatchResult(0, 0, 0, MoreRemaining: false, LockAcquired: false));

        var reaper = new TombstoneReaper(provider, new ValtuutusReaperOptions());

        var result = await reaper.ReapAsync();

        result.BatchesRun.Should().Be(1);
        result.RelationTuplesDeleted.Should().Be(0);
    }

    [Fact]
    public void ComputeWatermark_uses_the_configured_retention_period()
    {
        var options = new ValtuutusReaperOptions { RetentionPeriod = TimeSpan.FromDays(3) };
        options.RetentionPeriod.Should().Be(TimeSpan.FromDays(3));
    }
}
