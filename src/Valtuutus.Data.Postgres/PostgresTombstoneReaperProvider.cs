using Npgsql;
using NpgsqlTypes;
using Valtuutus.Core.Data;
using Valtuutus.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

internal sealed class PostgresTombstoneReaperProvider(DbConnectionFactory factory, IValtuutusDbOptions dbOptions)
    : ITombstoneReaperProvider
{
    // Fixed, compile-time constant — must NOT be derived from a randomized hash (e.g.
    // string.GetHashCode()/HashCode), which differs per process and would defeat coordination.
    private const long AdvisoryLockKey = 7_927_331_004_215_611L;

    public async Task<ReapBatchResult> ReapBatchAsync(SnapToken watermark, int batchSize, CancellationToken ct)
    {
        // Lock is acquired, used, and released within this one connection/call — scoped to this
        // batch only, not the whole sweep (see design doc "Multi-instance coordination").
        await using var connection = (NpgsqlConnection)factory();
        await connection.OpenAsync(ct);

        bool lockAcquired;
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.CommandText = "SELECT pg_try_advisory_lock(@key)";
            lockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            lockAcquired = (bool)(await lockCommand.ExecuteScalarAsync(ct))!;
        }

        if (!lockAcquired)
            return new ReapBatchResult(0, 0, 0, MoreRemaining: false, LockAcquired: false);

        try
        {
            var relationsDeleted = await ReapTable(connection, dbOptions.Schema, dbOptions.RelationsTableName,
                "deleted_tx_id IS NOT NULL AND deleted_tx_id < @watermark", watermark.Value, batchSize, ct);
            var attributesDeleted = await ReapTable(connection, dbOptions.Schema, dbOptions.AttributesTableName,
                "deleted_tx_id IS NOT NULL AND deleted_tx_id < @watermark", watermark.Value, batchSize, ct);
            var transactionsDeleted = await ReapTable(connection, dbOptions.Schema, dbOptions.TransactionsTableName,
                "id < @watermark", watermark.Value, batchSize, ct);

            var moreRemaining = relationsDeleted == batchSize || attributesDeleted == batchSize || transactionsDeleted == batchSize;
            return new ReapBatchResult(relationsDeleted, attributesDeleted, transactionsDeleted, moreRemaining, LockAcquired: true);
        }
        finally
        {
            // CancellationToken.None deliberately, not ct: this release runs precisely because ct
            // may have fired, so it must not itself be cancellable by the same token — otherwise a
            // cancelled batch would skip the explicit unlock and leave the lock held until the
            // connection teardown is separately noticed by Postgres, instead of releasing cleanly.
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandText = "SELECT pg_advisory_unlock(@key)";
            unlockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlockCommand.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private static async Task<int> ReapTable(NpgsqlConnection connection, string schema, string table,
        string predicate, string watermark, int batchSize, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM "{schema}"."{table}"
            WHERE ctid IN (
                SELECT ctid FROM "{schema}"."{table}"
                WHERE {predicate}
                LIMIT @batchSize
            )
            """;
        command.Parameters.Add(new NpgsqlParameter<string>("watermark", NpgsqlDbType.Char) { Size = 26, TypedValue = watermark });
        command.Parameters.Add(new NpgsqlParameter<int>("batchSize", batchSize));
        return await command.ExecuteNonQueryAsync(ct);
    }
}
