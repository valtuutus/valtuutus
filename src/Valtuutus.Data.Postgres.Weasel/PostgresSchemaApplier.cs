using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Valtuutus.Data.Db;
using Valtuutus.Data.Weasel;
using Weasel.Core;
using Weasel.Postgresql;

namespace Valtuutus.Data.Postgres.Weasel;

internal sealed class PostgresSchemaApplier(
    IServiceProvider services,
    ValtuutusPostgresOptions dbOptions,
    ValtuutusPostgresWeaselOptions options) : ISchemaApplier
{
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var logger = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance)
            .CreateLogger<PostgresSchemaApplier>();

        using var scope = services.CreateScope();
        var factory = options.ConnectionFactory?.Invoke(scope.ServiceProvider)
                      ?? scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
        var created = factory();
        if (created is not NpgsqlConnection typed)
        {
            created.Dispose();
            throw new InvalidOperationException(
                $"The Valtuutus schema is applied through Weasel, which needs an NpgsqlConnection; the connection factory returned {created.GetType().FullName}.");
        }

        await using var connection = typed;

        await connection.OpenAsync(cancellationToken);
        // The lock is polled: a statement blocked in a waiting lock call holds a transaction that the
        // concurrent index build of the node holding the lock waits on, which deadlocks the two.
        await LockPoller.AcquireAsync(
            async () => (await connection.TryGetGlobalLock(options.LockId, cancellationToken)).Succeeded,
            options.LockId, options.LockTimeout, cancellationToken);
        try
        {
            var migration = await SchemaMigration.DetermineAsync(connection, cancellationToken, PostgresSchema.Tables(dbOptions));
            if (migration.Difference != SchemaPatchDifference.None)
                await new PostgresqlMigrator().ApplyAllAsync(connection, migration, options.AutoCreate,
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
                NpgsqlConnection.ClearPool(connection);
            }
        }
    }
}
