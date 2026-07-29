using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory.Tests;

public class TombstoneReaperSpecs
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddInMemory().AddTombstoneReaper(o =>
        {
            o.RetentionPeriod = TimeSpan.Zero; // anything tombstoned "now" is immediately eligible
            o.PauseBetweenBatches = TimeSpan.Zero;
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReapAsync_removes_tombstones_older_than_retention_via_DI()
    {
        await using var provider = BuildProvider();
        var writer = provider.GetRequiredService<IDataWriterProvider>();
        await writer.Write([new RelationTuple("project", "p1", "owner", "user", "u1")], [], default);
        await writer.Delete(new DeleteFilter
        {
            Relations = [new DeleteRelationsFilter { EntityType = "project", EntityId = "p1" }]
        }, default);

        await Task.Delay(TimeSpan.FromMilliseconds(50)); // ensure the tombstone's tx clears a zero-retention watermark

        var reaper = provider.GetRequiredService<ITombstoneReaper>();
        var result = await reaper.ReapAsync();

        result.RelationTuplesDeleted.Should().Be(1);
        result.TransactionsDeleted.Should().Be(0);
    }

    [Fact]
    public async Task ReapAsync_never_removes_live_relations_or_attributes()
    {
        await using var provider = BuildProvider();
        var writer = provider.GetRequiredService<IDataWriterProvider>();
        await writer.Write(
            [new RelationTuple("project", "p1", "owner", "user", "u1")],
            [new AttributeTuple("project", "p1", "name", System.Text.Json.Nodes.JsonValue.Create("a")!)],
            default);

        var reaper = provider.GetRequiredService<ITombstoneReaper>();
        var result = await reaper.ReapAsync();

        result.RelationTuplesDeleted.Should().Be(0);
        result.AttributesDeleted.Should().Be(0);
    }

    [Fact]
    public async Task ReapBatchAsync_always_reports_LockAcquired_true_for_InMemory()
    {
        await using var provider = BuildProvider();
        var reaperProvider = provider.GetRequiredService<ITombstoneReaperProvider>();

        var result = await reaperProvider.ReapBatchAsync(SnapToken.MinValue, batchSize: 100, default);

        result.LockAcquired.Should().BeTrue();
    }
}
