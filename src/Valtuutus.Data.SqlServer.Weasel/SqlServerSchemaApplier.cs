using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Valtuutus.Data.Db;
using Valtuutus.Data.Weasel;
using Weasel.Core;
using Weasel.SqlServer;

namespace Valtuutus.Data.SqlServer.Weasel;

internal sealed class SqlServerSchemaApplier(
    IServiceProvider services,
    ValtuutusSqlServerOptions dbOptions,
    ValtuutusSqlServerWeaselOptions options) : ISchemaApplier
{
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<SqlServerSchemaApplier>();

        using var scope = services.CreateScope();
        var factory = options.ConnectionFactory?.Invoke(scope.ServiceProvider)
                      ?? scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
        var created = factory();
        if (created is not SqlConnection typed)
        {
            created.Dispose();
            throw new InvalidOperationException(
                $"The Valtuutus schema is applied through Weasel, which needs a SqlConnection; the connection factory returned {created.GetType().FullName}.");
        }

        await using var connection = typed;

        await connection.OpenAsync(cancellationToken);
        // The lock is polled so that cancellation ends the wait; cancelling a blocked sp_getapplock
        // surfaces as a SqlException.
        await LockPoller.AcquireAsync(
            () => connection.TryGetGlobalLock(options.LockId, cancellationToken, lockTimeoutMs: 0),
            options.LockId, options.LockTimeout, cancellationToken);
        try
        {
            var migration = await SchemaMigration.DetermineAsync(connection, cancellationToken, SqlServerSchema.Tables(dbOptions));
            if (migration.Difference != SchemaPatchDifference.None)
                await new SqlServerMigrator().ApplyAllAsync(connection, migration, options.AutoCreate,
                    new WeaselMigrationLogger(logger), cancellationToken);
        }
        finally
        {
            // The lock is held by the session, so a failed release must not replace the exception that
            // is already propagating; the connection is removed from the pool so its session, and the
            // lock with it, ends.
            try
            {
                await connection.ReleaseGlobalLock(options.LockId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Releasing the Valtuutus schema lock failed; the connection is removed from the pool to end it.");
                SqlConnection.ClearPool(connection);
            }
        }
    }
}
