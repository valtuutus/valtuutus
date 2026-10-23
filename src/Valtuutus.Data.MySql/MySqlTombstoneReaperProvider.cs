using MySqlConnector;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql;

internal sealed class MySqlTombstoneReaperProvider(DbConnectionFactory factory, IValtuutusDbOptions dbOptions)
    : ITombstoneReaperProvider
{
    // Named locks are server-wide, so the name carries the database the tables live in: instances
    // over the same database share one lock, and databases on the same server don't block each
    // other. The database is hashed to keep the name within the server's limit on lock names.
    // @schema binds the configured schema; an empty one means the connection's own database.
    internal const string LockName =
        "CONCAT('valtuutus_reaper_', MD5(COALESCE(NULLIF(@schema, ''), DATABASE())))";

    public async Task<ReapBatchResult> ReapBatchAsync(SnapToken watermark, int batchSize, CancellationToken ct)
    {
        // The lock belongs to this connection's session and is held for this one batch only.
        await using var connection = (MySqlConnection)factory();
        await connection.OpenAsync(ct);

        bool lockAcquired;
        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.CommandText = $"SELECT GET_LOCK({LockName}, 0)";
            AddSchema(lockCommand.Parameters);
            lockAcquired = Convert.ToInt64(await lockCommand.ExecuteScalarAsync(ct)) == 1;
        }

        if (!lockAcquired)
            return new ReapBatchResult(0, 0, 0, MoreRemaining: false, LockAcquired: false);

        try
        {
            var relations = MySqlConnections.Relations(dbOptions);
            var attributes = MySqlConnections.Attributes(dbOptions);
            var transactions = MySqlConnections.Transactions(dbOptions);

            var relationsDeleted = await ReapTable(connection,
                $"DELETE FROM {relations} WHERE deleted_tx_id IS NOT NULL AND deleted_tx_id < @watermark ORDER BY id LIMIT @batchSize",
                watermark.Value, batchSize, ct);
            // The primary key leads with the attribute's key columns, so ordering by id would sort
            // every candidate row; any batch of tombstones serves.
            var attributesDeleted = await ReapTable(connection,
                $"DELETE FROM {attributes} WHERE deleted_tx_id IS NOT NULL AND deleted_tx_id < @watermark LIMIT @batchSize",
                watermark.Value, batchSize, ct);
            var transactionsDeleted = await ReapTable(connection,
                $"DELETE FROM {transactions} WHERE id < @watermark ORDER BY id LIMIT @batchSize",
                watermark.Value, batchSize, ct);

            var moreRemaining = relationsDeleted == batchSize || attributesDeleted == batchSize || transactionsDeleted == batchSize;
            return new ReapBatchResult(relationsDeleted, attributesDeleted, transactionsDeleted, moreRemaining, LockAcquired: true);
        }
        finally
        {
            // Released with CancellationToken.None: this runs when ct has fired too, and a pooled
            // session would otherwise keep holding the lock.
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandText = $"SELECT RELEASE_LOCK({LockName})";
            AddSchema(unlockCommand.Parameters);
            await unlockCommand.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private void AddSchema(MySqlParameterCollection parameters) =>
        parameters.Add(new MySqlParameter("@schema", MySqlDbType.VarChar) { Value = dbOptions.Schema });

    // LIMIT takes the batch size as an integer literal, which MySqlConnector writes for an Int32
    // parameter.
    private static async Task<int> ReapTable(MySqlConnection connection, string sql, string watermark, int batchSize,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new MySqlParameter("@watermark", MySqlDbType.String) { Size = 26, Value = watermark });
        command.Parameters.Add(new MySqlParameter("@batchSize", MySqlDbType.Int32) { Value = batchSize });
        return await command.ExecuteNonQueryAsync(ct);
    }
}
