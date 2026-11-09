using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Valtuutus.Data.Db;
using Valtuutus.Data.Weasel;
using Weasel.Core;
using Weasel.MySql;

namespace Valtuutus.Data.MySql.Weasel;

internal sealed class MySqlSchemaApplier(
    IServiceProvider services,
    ValtuutusMySqlOptions dbOptions,
    ValtuutusMySqlWeaselOptions options) : ISchemaApplier
{
    // Named locks are server-wide, so the name carries the database the tables live in: applies over
    // the same database serialize, and databases on the same server don't block each other. The
    // database is hashed to keep the name within the server's limit on lock names.
    internal const string LockName = "CONCAT(@name, '_', MD5(@database))";

    // The server's limit on lock names, less the separator and the hash.
    internal const int MaxLockIdLength = 64 - 1 - 32;

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        if (options.LockId.Length > MaxLockIdLength)
            throw new InvalidOperationException(
                $"The Valtuutus schema lock id '{options.LockId}' is longer than {MaxLockIdLength} characters.");

        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<MySqlSchemaApplier>();

        using var scope = services.CreateScope();
        var factory = options.ConnectionFactory?.Invoke(scope.ServiceProvider)
                      ?? scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
        var created = factory();
        if (created is not MySqlConnection typed)
        {
            created.Dispose();
            throw new InvalidOperationException(
                $"The Valtuutus schema is applied through Weasel, which needs a MySqlConnection; the connection factory returned {created.GetType().FullName}.");
        }

        await using var connection = typed;

        await connection.OpenAsync(cancellationToken);
        var database = string.IsNullOrEmpty(dbOptions.Schema) ? connection.Database : dbOptions.Schema;

        // The lock is polled so that cancellation ends the wait on every server.
        await LockPoller.AcquireAsync(async () =>
        {
            await using var acquire = new MySqlCommand($"SELECT GET_LOCK({LockName}, 0)", connection);
            AddLockParameters(acquire, database);
            var result = await acquire.ExecuteScalarAsync(cancellationToken);
            return result is not null and not DBNull && Convert.ToInt64(result) == 1;
        }, options.LockId, options.LockTimeout, cancellationToken);
        try
        {
            var migration = await SchemaMigration.DetermineAsync(connection, cancellationToken, MySqlSchema.Tables(dbOptions, database));
            if (migration.Difference != SchemaPatchDifference.None)
                await new MySqlMigrator().ApplyAllAsync(connection, migration, options.AutoCreate,
                    new WeaselMigrationLogger(logger), cancellationToken);
        }
        finally
        {
            // The lock is held by the session, so a failed release must not replace the exception that
            // is already propagating; the connection is removed from the pool so its session, and the
            // lock with it, ends.
            try
            {
                await using var release = new MySqlCommand($"SELECT RELEASE_LOCK({LockName})", connection);
                AddLockParameters(release, database);
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Releasing the Valtuutus schema lock failed; the connection is removed from the pool to end it.");
                MySqlConnection.ClearPool(connection);
            }
        }
    }

    private void AddLockParameters(MySqlCommand command, string database)
    {
        command.Parameters.AddWithValue("name", options.LockId);
        command.Parameters.AddWithValue("database", database);
    }
}
