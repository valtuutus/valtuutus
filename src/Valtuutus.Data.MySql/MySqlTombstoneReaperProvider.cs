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

    private static readonly MySqlStatement Lock = MySqlStatement.Compile($"SELECT GET_LOCK({LockName}, 0)");
    private static readonly MySqlStatement Unlock = MySqlStatement.Compile($"SELECT RELEASE_LOCK({LockName})");

    private readonly MySqlStatement _reapRelations = MySqlStatement.Compile(
        $"DELETE FROM {MySqlConnections.Relations(dbOptions)} WHERE deleted_tx_id IS NOT NULL AND deleted_tx_id < @watermark ORDER BY id LIMIT @batchSize");

    // The primary key leads with the attribute's key columns, so ordering by id would sort every
    // candidate row; any batch of tombstones serves.
    private readonly MySqlStatement _reapAttributes = MySqlStatement.Compile(
        $"DELETE FROM {MySqlConnections.Attributes(dbOptions)} WHERE deleted_tx_id IS NOT NULL AND deleted_tx_id < @watermark LIMIT @batchSize");

    private readonly MySqlStatement _reapTransactions = MySqlStatement.Compile(
        $"DELETE FROM {MySqlConnections.Transactions(dbOptions)} WHERE id < @watermark ORDER BY id LIMIT @batchSize");

    public async Task<ReapBatchResult> ReapBatchAsync(SnapToken watermark, int batchSize, CancellationToken ct)
    {
        // The lock belongs to this connection's session and is held for this one batch only.
        await using var connection = (MySqlConnection)factory();
        await connection.OpenAsync(ct);

        bool lockAcquired;
        await using (var lockCommand = connection.CreateCommand())
        {
            AddSchema(lockCommand.Parameters);
            Lock.ApplyTo(lockCommand);
            lockAcquired = Convert.ToInt64(await lockCommand.ExecuteScalarAsync(ct)) == 1;
        }

        if (!lockAcquired)
            return new ReapBatchResult(0, 0, 0, MoreRemaining: false, LockAcquired: false);

        try
        {
            var relationsDeleted = await ReapTable(connection, _reapRelations, watermark.Value, batchSize, ct);
            var attributesDeleted = await ReapTable(connection, _reapAttributes, watermark.Value, batchSize, ct);
            var transactionsDeleted = await ReapTable(connection, _reapTransactions, watermark.Value, batchSize, ct);

            var moreRemaining = relationsDeleted == batchSize || attributesDeleted == batchSize || transactionsDeleted == batchSize;
            return new ReapBatchResult(relationsDeleted, attributesDeleted, transactionsDeleted, moreRemaining, LockAcquired: true);
        }
        finally
        {
            // Released with CancellationToken.None: this runs when ct has fired too, and a pooled
            // session would otherwise keep holding the lock.
            await using var unlockCommand = connection.CreateCommand();
            AddSchema(unlockCommand.Parameters);
            Unlock.ApplyTo(unlockCommand);
            await unlockCommand.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private void AddSchema(MySqlParameterCollection parameters) =>
        parameters.Add(new MySqlParameter("schema", MySqlDbType.VarChar) { Value = dbOptions.Schema });

    // LIMIT takes the batch size as an integer literal, which MySqlConnector writes for an Int32
    // parameter.
    private static async Task<int> ReapTable(MySqlConnection connection, MySqlStatement statement, string watermark,
        int batchSize, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Parameters.Add(new MySqlParameter("watermark", MySqlDbType.String) { Size = 26, Value = watermark });
        command.Parameters.Add(new MySqlParameter("batchSize", MySqlDbType.Int32) { Value = batchSize });
        statement.ApplyTo(command);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
