using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Data;

namespace Valtuutus.Data.MySql.Tests
{
    public abstract class TombstoneReaperSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        private ServiceProvider _provider = default!;

        public Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddMySql(_ => fixture.DbFactory, fixture.Options)
                .AddTombstoneReaper(o =>
                {
                    o.RetentionPeriod = TimeSpan.Zero;
                    o.BatchSize = 2;
                    o.PauseBetweenBatches = TimeSpan.Zero;
                });
            _provider = services.BuildServiceProvider();
            return fixture.ResetDatabaseAsync();
        }

        public async Task DisposeAsync()
        {
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
            // Only tombstoned rows are eligible. A live row (deleted_tx_id IS NULL) must never be
            // removed, no matter how old its created_tx_id is.
            var writer = _provider.GetRequiredService<IDataWriterProvider>();
            await writer.Write([new RelationTuple("project", "p1", "owner", "user", "u1")], [], default);

            var reaper = _provider.GetRequiredService<ITombstoneReaper>();
            var result = await reaper.ReapAsync();

            result.RelationTuplesDeleted.Should().Be(0);

            await using var db = (MySqlConnection)fixture.DbFactory();
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM relation_tuples WHERE entity_id = 'p1' AND deleted_tx_id IS NULL)";
            var stillLive = Convert.ToBoolean(await command.ExecuteScalarAsync());
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
            // Each Write+Delete pair also inserts 2 transactions rows, so 10 transactions rows are
            // eligible and batched the same way; draining those needs 5 full batches plus a 6th to
            // observe 0 remaining, which governs BatchesRun.
            result.RelationTuplesDeleted.Should().Be(5);
            result.BatchesRun.Should().Be(6);
        }

        [Fact]
        public async Task ReapBatchAsync_deletes_tombstones_of_every_table_and_releases_its_lock()
        {
            // Without a reset on reuse, a pooled session keeps every lock it still holds, so only
            // the provider's own release frees the lock for another session.
            var noReset = new MySqlConnectionStringBuilder(fixture.ConnectionString) { ConnectionReset = false }.ConnectionString;
            await using var provider = Services(noReset, fixture.Options);
            var writer = provider.GetRequiredService<IDataWriterProvider>();
            await writer.Write([new RelationTuple("project", "p1", "owner", "user", "u1")],
                [new AttributeTuple("project", "p1", "public", System.Text.Json.Nodes.JsonValue.Create(true))], default);
            await writer.Write([], [new AttributeTuple("project", "p1", "public", System.Text.Json.Nodes.JsonValue.Create(false))], default);
            await writer.Delete(new DeleteFilter
            {
                Relations = [new DeleteRelationsFilter { EntityType = "project", EntityId = "p1" }]
            }, default);
            // Transaction ids order by millisecond only, so the watermark is taken in a later one.
            await Task.Delay(TimeSpan.FromMilliseconds(10));
            var watermark = await writer.Write([], [], default);

            var reaperProvider = provider.GetRequiredService<ITombstoneReaperProvider>();
            var first = await reaperProvider.ReapBatchAsync(watermark, batchSize: 100, default);
            var second = await reaperProvider.ReapBatchAsync(watermark, batchSize: 100, default);

            first.Should().Be(new ReapBatchResult(1, 1, 3, MoreRemaining: false, LockAcquired: true));
            second.Should().Be(new ReapBatchResult(0, 0, 0, MoreRemaining: false, LockAcquired: true));

            await using var otherSession = await OpenUnpooled(fixture.ConnectionString);
            (await TryLock(otherSession, schema: "")).Should().Be(1);
            await ReleaseLock(otherSession, schema: "");
        }

        [Fact]
        public async Task ReapBatchAsync_over_another_database_on_the_same_server_takes_a_lock_of_its_own()
        {
            const string secondDatabase = "valtuutus_second";
            var secondConnectionString = await fixture.CreateDatabaseAsync(secondDatabase);

            // A session holds the lock of the fixture's database, as an instance mid-batch over it
            // would.
            await using var holdingConnection = await OpenUnpooled(fixture.ConnectionString);
            (await TryLock(holdingConnection, schema: "")).Should().Be(1);

            try
            {
                await using var overSecond = Services(secondConnectionString, fixture.Options);
                await using var secondBySchema = Services(fixture.ConnectionString, SchemaOptions(secondDatabase));
                await using var firstBySchema = Services(fixture.ConnectionString,
                    SchemaOptions(new MySqlConnectionStringBuilder(fixture.ConnectionString).Database));

                (await ReapNothing(overSecond)).LockAcquired.Should().BeTrue();
                (await ReapNothing(secondBySchema)).LockAcquired.Should().BeTrue();
                (await ReapNothing(firstBySchema)).LockAcquired.Should().BeFalse();
                (await ReapNothing(_provider)).LockAcquired.Should().BeFalse();
            }
            finally
            {
                await ReleaseLock(holdingConnection, schema: "");
            }
        }

        [Fact]
        public async Task ReapBatchAsync_keeps_a_tombstone_exactly_at_the_watermark()
        {
            // The delete predicate is strict (deleted_tx_id < @watermark). Using the tombstone's own
            // deleted_tx_id as the watermark must therefore keep it.
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
            // A second, independent session holds the same named lock, simulating a concurrent
            // instance mid-batch.
            await using var holdingConnection = await OpenUnpooled(fixture.ConnectionString);
            (await TryLock(holdingConnection, schema: "")).Should().Be(1);

            try
            {
                var reaperProvider = _provider.GetRequiredService<ITombstoneReaperProvider>();
                var result = await reaperProvider.ReapBatchAsync(SnapToken.MinValue, batchSize: 100, default);

                result.LockAcquired.Should().BeFalse();
                result.RelationTuplesDeleted.Should().Be(0);
            }
            finally
            {
                await ReleaseLock(holdingConnection, schema: "");
            }
        }

        private ValtuutusMySqlOptions SchemaOptions(string schema) =>
            new(fixture.ServerType, schema, "transactions", "relation_tuples", "attributes");

        private static ServiceProvider Services(string connectionString, ValtuutusMySqlOptions options)
        {
            var services = new ServiceCollection();
            services.AddMySql(_ => () => new MySqlConnection(connectionString), options);
            return services.BuildServiceProvider();
        }

        private static Task<ReapBatchResult> ReapNothing(IServiceProvider provider) =>
            provider.GetRequiredService<ITombstoneReaperProvider>()
                .ReapBatchAsync(SnapToken.MinValue, batchSize: 100, default);

        private static async Task<MySqlConnection> OpenUnpooled(string connectionString)
        {
            var connection = new MySqlConnection(
                new MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            return connection;
        }

        private static async Task<long> TryLock(MySqlConnection connection, string schema)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT GET_LOCK({MySqlTombstoneReaperProvider.LockName}, 0)";
            command.Parameters.AddWithValue("@schema", schema);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        private static async Task ReleaseLock(MySqlConnection connection, string schema)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT RELEASE_LOCK({MySqlTombstoneReaperProvider.LockName})";
            command.Parameters.AddWithValue("@schema", schema);
            await command.ExecuteScalarAsync();
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class TombstoneReaperSpecs(MySqlFixture fixture) : TombstoneReaperSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class TombstoneReaperSpecs(MariaDbFixture fixture) : TombstoneReaperSpecsBase(fixture);
}
