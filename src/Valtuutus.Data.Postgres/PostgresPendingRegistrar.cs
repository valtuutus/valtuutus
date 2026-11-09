using System.Collections.Concurrent;
using System.Threading.Channels;
using Npgsql;
using NpgsqlTypes;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

/// <summary>
/// Registers in-flight writes so readers resolve only settled snap tokens. A write registers the id
/// it will commit at or above, together with its transaction's xid, in its own transaction that
/// commits at once: from then on readers stop below it until the write's pending row is gone or its
/// transaction is no longer in progress.
/// </summary>
/// <remarks>
/// Writes that arrive while a registration is being committed are committed together in the next
/// one, so concurrent writes share one side transaction. Each registration also returns the newest
/// committed id, read after the registration committed: a write takes an id above it, so a reader
/// that resolved its token before the registration became visible never settled above the write.
/// One registrar serves each database, keyed by the shared <see cref="NpgsqlDataSource"/>.
/// </remarks>
internal sealed class PostgresPendingRegistrar
{
    private static readonly ConcurrentDictionary<(NpgsqlDataSource, DbQueryCacheKey), PostgresPendingRegistrar> Registrars = new();

    private readonly Channel<Request> _requests =
        Channel.CreateUnbounded<Request>(new UnboundedChannelOptions { SingleReader = true });
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _insert;
    private readonly string _newestCommitted;
    private readonly string _collectFinished;

    private sealed record Request(string LowerBound, string Xid, TaskCompletionSource<string?> Done);

    private PostgresPendingRegistrar(NpgsqlDataSource dataSource, DbQueryCacheKey key)
    {
        _dataSource = dataSource;
        var pending = PendingTable(key);
        _insert = $"INSERT INTO {pending} (lower_bound, xid) SELECT * FROM unnest(@lower_bounds::bpchar[], @xids::text[]::xid8[])";
        _newestCommitted = $"SELECT id FROM {key.Schema}.{key.TransactionsTable} ORDER BY id DESC LIMIT 1";
        // Rows of writes that rolled back or whose connection died stay behind; their transaction is
        // no longer in progress. Rows are locked while being collected so concurrent registrations
        // skip each other's instead of waiting.
        _collectFinished = $"""
            DELETE FROM {pending} WHERE id IN (
                SELECT id FROM {pending}
                WHERE pg_xact_status(xid) IS DISTINCT FROM 'in progress'
                ORDER BY id LIMIT 16 FOR UPDATE SKIP LOCKED)
            """;
        _ = Task.Run(RunAsync);
    }

    internal static string PendingTable(DbQueryCacheKey key) => $"{key.Schema}.{key.TransactionsTable}_pending";

    public static PostgresPendingRegistrar For(NpgsqlDataSource dataSource, DbQueryCacheKey key) =>
        Registrars.GetOrAdd((dataSource, key), static k => new PostgresPendingRegistrar(k.Item1, k.Item2));

    /// <summary>
    /// Registers a write that will commit at <paramref name="lowerBound"/> or above, in the transaction
    /// <paramref name="xid"/>. Returns the newest committed id, read after the registration committed.
    /// </summary>
    public Task<string?> RegisterAsync(string lowerBound, string xid, CancellationToken ct)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Writer.TryWrite(new Request(lowerBound, xid, done));
        return done.Task.WaitAsync(ct);
    }

    private async Task RunAsync()
    {
        var group = new List<Request>();
        while (await _requests.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            group.Clear();
            while (_requests.Reader.TryRead(out var request)) group.Add(request);
            try
            {
                var newest = await FlushAsync(group).ConfigureAwait(false);
                foreach (var request in group) request.Done.TrySetResult(newest);
            }
            catch (Exception e)
            {
                foreach (var request in group) request.Done.TrySetException(e);
            }
        }
    }

    private async Task<string?> FlushAsync(List<Request> group)
    {
        var lowerBounds = new string[group.Count];
        var xids = new string[group.Count];
        for (var i = 0; i < group.Count; i++)
        {
            lowerBounds[i] = group[i].LowerBound;
            xids[i] = group[i].Xid;
        }

        await using var connection = await _dataSource.OpenConnectionAsync().ConfigureAwait(false);
        await using var batch = connection.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("BEGIN"));
        // A crash that loses this commit also ends the writes it registers, so it needn't wait for
        // the flush to disk.
        batch.BatchCommands.Add(new NpgsqlBatchCommand("SET LOCAL synchronous_commit = off"));
        var insert = new NpgsqlBatchCommand(_insert);
        insert.Parameters.Add(new NpgsqlParameter<string[]>("lower_bounds", NpgsqlDbType.Array | NpgsqlDbType.Char) { TypedValue = lowerBounds });
        insert.Parameters.Add(new NpgsqlParameter<string[]>("xids", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = xids });
        batch.BatchCommands.Add(insert);
        batch.BatchCommands.Add(new NpgsqlBatchCommand("COMMIT"));
        // Runs in a transaction of its own, after the registration committed.
        batch.BatchCommands.Add(new NpgsqlBatchCommand(_newestCommitted));
        batch.BatchCommands.Add(new NpgsqlBatchCommand(_collectFinished));

        await using var reader = await batch.ExecuteReaderAsync().ConfigureAwait(false);
        string? newest = null;
        do
        {
            if (reader.FieldCount == 1 && await reader.ReadAsync().ConfigureAwait(false) && !reader.IsDBNull(0))
                newest = reader.GetString(0);
        } while (await reader.NextResultAsync().ConfigureAwait(false));
        return newest;
    }
}
