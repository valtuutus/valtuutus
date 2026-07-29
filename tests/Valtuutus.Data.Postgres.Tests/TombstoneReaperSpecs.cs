using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public class TombstoneReaperSpecs : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private ServiceProvider _provider = default!;

    public TombstoneReaperSpecs(PostgresFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddPostgres(_ => ((IWithDbConnectionFactory)_fixture).DbFactory)
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
        await Task.Delay(TimeSpan.FromMilliseconds(10)); // ensure tombstone tx predates a zero-retention watermark

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

        await using var db = (Npgsql.NpgsqlConnection)((IWithDbConnectionFactory)_fixture).DbFactory();
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM public.relation_tuples WHERE entity_id = 'p1' AND deleted_tx_id IS NULL)";
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

        // BatchSize = 2, 5 relation tombstones -> 3 batches to drain relation_tuples alone.
        // But each Write+Delete pair also inserts 2 transactions rows (one per call), so 10
        // transactions rows are independently eligible (id < watermark) and batched the same
        // way; draining those needs 5 full batches plus a 6th to observe 0 remaining, which
        // is the larger number and therefore governs BatchesRun.
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
        // Open a second, independent connection and hold the same advisory lock key on it directly,
        // simulating a concurrent instance mid-batch.
        await using var holdingConnection = (Npgsql.NpgsqlConnection)((IWithDbConnectionFactory)_fixture).DbFactory();
        await holdingConnection.OpenAsync();
        await using (var lockCommand = holdingConnection.CreateCommand())
        {
            lockCommand.CommandText = "SELECT pg_try_advisory_lock(7927331004215611)";
            var acquired = (bool)(await lockCommand.ExecuteScalarAsync())!;
            acquired.Should().BeTrue();
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
            // Npgsql's default pooling means disposing holdingConnection returns the physical
            // backend to the pool without ending the Postgres session — advisory locks only
            // release on session end or explicit unlock, so without this the lock would leak
            // onto a pooled connection and could spuriously fail unrelated future test runs.
            await using var unlockCommand = holdingConnection.CreateCommand();
            unlockCommand.CommandText = "SELECT pg_advisory_unlock(7927331004215611)";
            await unlockCommand.ExecuteScalarAsync();
        }
    }
}
