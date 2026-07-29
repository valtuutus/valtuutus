using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public class TombstoneReaperSpecs : IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;
    private ServiceProvider _provider = default!;

    public TombstoneReaperSpecs(SqlServerFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSqlServer(_ => ((IWithDbConnectionFactory)_fixture).DbFactory)
            .AddTombstoneReaper(o =>
            {
                o.RetentionPeriod = TimeSpan.Zero;
                o.BatchSize = 2;
                o.PauseBetweenBatches = TimeSpan.Zero;
            });
        _provider = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.ResetDatabaseAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task ReapAsync_deletes_tombstones_older_than_the_watermark()
    {
        var writer = _provider.GetRequiredService<IDataWriterProvider>();
        await writer.Write([new RelationTuple("project", "p1", "owner", "user", "u1")], [], default);
        await writer.Delete(new DeleteFilter
        {
            Relations = [new DeleteRelationsFilter { EntityType = "project", EntityId = "p1" }]
        }, default);
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        var reaper = _provider.GetRequiredService<ITombstoneReaper>();
        var result = await reaper.ReapAsync();

        result.RelationTuplesDeleted.Should().Be(1);
    }

    [Fact]
    public async Task ReapAsync_never_removes_a_live_row_regardless_of_its_creation_age()
    {
        // Critical invariant: only tombstoned rows are eligible. A live row (deleted_tx_id IS
        // NULL) must never be removed, no matter how old its created_tx_id is — getting this
        // backwards would prune live production authorization data.
        var writer = _provider.GetRequiredService<IDataWriterProvider>();
        await writer.Write([new RelationTuple("project", "p1", "owner", "user", "u1")], [], default);

        var reaper = _provider.GetRequiredService<ITombstoneReaper>();
        var result = await reaper.ReapAsync();

        result.RelationTuplesDeleted.Should().Be(0);

        await using var db = (Microsoft.Data.SqlClient.SqlConnection)((IWithDbConnectionFactory)_fixture).DbFactory();
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM relation_tuples WHERE entity_id = 'p1' AND deleted_tx_id IS NULL) THEN 1 ELSE 0 END AS BIT)";
        var stillLive = (bool)(await command.ExecuteScalarAsync())!;
        stillLive.Should().BeTrue();
    }

    [Fact]
    public async Task ReapAsync_loops_across_multiple_batches()
    {
        var writer = _provider.GetRequiredService<IDataWriterProvider>();
        for (int i = 0; i < 5; i++)
        {
            await writer.Write([new RelationTuple("project", $"p{i}", "owner", "user", "u1")], [], default);
            await writer.Delete(new DeleteFilter
            {
                Relations = [new DeleteRelationsFilter { EntityType = "project", EntityId = $"p{i}" }]
            }, default);
        }
        await Task.Delay(TimeSpan.FromMilliseconds(10));

        var reaper = _provider.GetRequiredService<ITombstoneReaper>();
        var result = await reaper.ReapAsync();

        // BatchSize=2. 5 relation tombstones AND 10 transaction rows (each Write+Delete pair
        // inserts 2 transaction rows — one per call, both via InsertTransaction, confirmed in
        // SqlServerDataWriterProvider.cs). MoreRemaining is an OR across all three tables' delete
        // counts, and transactions (10 rows) drains slower than relations (5 rows) at batchSize=2:
        // 5 full batches to drain transactions, plus a 6th to observe nothing left. This was
        // verified against the identical Postgres version of this test (Task 6), where the
        // plan's original guess of 3 (reasoning from relations alone) was found and fixed to 6.
        result.RelationTuplesDeleted.Should().Be(5);
        result.BatchesRun.Should().Be(6);
    }

    [Fact]
    public async Task ReapBatchAsync_keeps_a_tombstone_exactly_at_the_watermark()
    {
        // The delete predicate is strict (deleted_tx_id < @watermark). Using the tombstone's own
        // deleted_tx_id as the watermark must therefore keep it: it is not strictly older than itself.
        var writer = _provider.GetRequiredService<IDataWriterProvider>();
        await writer.Write([new RelationTuple("project", "p1", "owner", "user", "u1")], [], default);
        var deleteSnapToken = await writer.Delete(new DeleteFilter
        {
            Relations = [new DeleteRelationsFilter { EntityType = "project", EntityId = "p1" }]
        }, default);

        var reaperProvider = _provider.GetRequiredService<ITombstoneReaperProvider>();
        var result = await reaperProvider.ReapBatchAsync(deleteSnapToken, batchSize: 100, default);

        result.RelationTuplesDeleted.Should().Be(0);
    }

    [Fact]
    public async Task ReapBatchAsync_reports_LockAcquired_false_when_another_session_already_holds_it()
    {
        // Open a second, independent connection and hold the same applock resource on it directly,
        // simulating a concurrent instance mid-batch. Explicitly release in finally — disposing the
        // connection alone returns it to the pool without ending the session/releasing the applock
        // (same pooling gotcha as the Postgres version of this test).
        await using var holdingConnection = (Microsoft.Data.SqlClient.SqlConnection)((IWithDbConnectionFactory)_fixture).DbFactory();
        await holdingConnection.OpenAsync();
        await using (var lockCommand = holdingConnection.CreateCommand())
        {
            lockCommand.CommandText = """
                DECLARE @result int;
                EXEC @result = sp_getapplock @Resource = 'Valtuutus:TombstoneReaper', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
                SELECT @result;
                """;
            var acquired = (int)(await lockCommand.ExecuteScalarAsync())!;
            acquired.Should().BeGreaterThanOrEqualTo(0);
        }

        try
        {
            var reaperProvider = _provider.GetRequiredService<ITombstoneReaperProvider>();
            var result = await reaperProvider.ReapBatchAsync(SnapToken.MinValue, batchSize: 100, default);

            result.LockAcquired.Should().BeFalse();
            result.RelationTuplesDeleted.Should().Be(0);
        }
        finally
        {
            await using var unlockCommand = holdingConnection.CreateCommand();
            unlockCommand.CommandText = "EXEC sp_releaseapplock @Resource = 'Valtuutus:TombstoneReaper', @LockOwner = 'Session'";
            await unlockCommand.ExecuteNonQueryAsync();
        }
    }
}
