using Microsoft.Data.SqlClient;
using Valtuutus.Core.Data;
using Valtuutus.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;

internal sealed class SqlServerTombstoneReaperProvider(DbConnectionFactory factory, IValtuutusDbOptions dbOptions)
    : ITombstoneReaperProvider
{
    // Fixed, compile-time constant — must NOT be derived from a randomized hash, which differs
    // per process and would defeat coordination across instances.
    private const string AdvisoryLockResource = "Valtuutus:TombstoneReaper";

    public async Task<ReapBatchResult> ReapBatchAsync(SnapToken watermark, int batchSize, CancellationToken ct)
    {
        // Lock is acquired, used, and released within this one connection/call — scoped to this
        // batch only, not the whole sweep (see design doc "Multi-instance coordination").
        await using var connection = (SqlConnection)factory();
        await connection.OpenAsync(ct);

        int lockResult;
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.CommandText = """
                DECLARE @result int;
                EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
                SELECT @result;
                """;
            lockCommand.Parameters.AddWithValue("resource", AdvisoryLockResource);
            lockResult = (int)(await lockCommand.ExecuteScalarAsync(ct))!;
        }

        // sp_getapplock: 0 or 1 = acquired successfully, negative = failed/timed out.
        if (lockResult < 0)
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
            // connection teardown is separately noticed by SQL Server, instead of releasing cleanly.
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session'";
            unlockCommand.Parameters.AddWithValue("resource", AdvisoryLockResource);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task<int> ReapTable(SqlConnection connection, string schema, string table,
        string predicate, string watermark, int batchSize, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE TOP (@batchSize) FROM [{schema}].[{table}]
            WHERE {predicate}
            """;
        command.Parameters.Add(new SqlParameter("watermark", System.Data.SqlDbType.NChar, 26) { Value = watermark });
        command.Parameters.Add(new SqlParameter("batchSize", System.Data.SqlDbType.Int) { Value = batchSize });
        return await command.ExecuteNonQueryAsync(ct);
    }
}
