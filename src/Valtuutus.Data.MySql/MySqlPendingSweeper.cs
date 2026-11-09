using System.Collections.Concurrent;
using System.Data;
using MySqlConnector;

namespace Valtuutus.Data.MySql;

/// <summary>
/// Removes the pending rows of writes that ended without removing their own: a write deletes its
/// pending row in its own transaction, so a rollback or a dropped connection brings the row back,
/// and readers would stop below it for good. A row no transaction holds a lock on belongs to a write
/// that ended; it is removed once it is older than the time between a write's registration and its
/// delete taking the lock.
/// </summary>
/// <remarks>
/// One sweeper runs per database, keyed by the shared <see cref="MySqlDataSource"/>, independently
/// of whether writes keep coming, so a write that rolled back stops holding readers back even when
/// it was the last one.
/// </remarks>
internal sealed class MySqlPendingSweeper
{
    private static readonly ConcurrentDictionary<(MySqlDataSource, string), MySqlPendingSweeper> Sweepers = new();
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly MySqlDataSource _dataSource;
    private readonly string _findEnded;
    private readonly string _delete;

    private MySqlPendingSweeper(MySqlDataSource dataSource, string pendingTable)
    {
        _dataSource = dataSource;
        // Read committed: the locking read takes no gap locks, so registrations aren't blocked.
        _findEnded = $"""
            SELECT slot FROM {pendingTable}
            WHERE lower_bound <> '~~~~~~~~~~~~~~~~~~~~~~~~~~' AND claimed_at < UTC_TIMESTAMP(6) - INTERVAL 1 SECOND
            LIMIT 64 FOR UPDATE SKIP LOCKED
            """;
        _delete = $"UPDATE {pendingTable} SET lower_bound = '~~~~~~~~~~~~~~~~~~~~~~~~~~' WHERE slot = @id";
        _ = Task.Run(RunAsync);
    }

    public static void Ensure(MySqlDataSource dataSource, string pendingTable) =>
        Sweepers.GetOrAdd((dataSource, pendingTable), static k => new MySqlPendingSweeper(k.Item1, k.Item2));

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            try
            {
                await SweepAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The next tick retries; an unreachable database or a missing table must not stop it.
            }
        }
    }

    private async Task SweepAsync()
    {
        await using var connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
        var ended = new List<long>();
        await using (var find = new MySqlCommand(_findEnded, connection, transaction))
        await using (var reader = await find.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false)) ended.Add(reader.GetInt64(0));
        }

        foreach (var id in ended)
        {
            await using var delete = new MySqlCommand(_delete, connection, transaction);
            delete.Parameters.AddWithValue("id", id);
            await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await transaction.CommitAsync().ConfigureAwait(false);
    }
}
