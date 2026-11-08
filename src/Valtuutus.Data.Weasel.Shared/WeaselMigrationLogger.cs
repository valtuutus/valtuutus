using System.Data.Common;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Weasel.Core.Migrations;

namespace Valtuutus.Data.Weasel;

/// <summary>Routes Weasel's migration output into <see cref="ILogger"/>.</summary>
internal sealed class WeaselMigrationLogger(ILogger logger) : IMigrationLogger
{
    public void SchemaChange(string sql) => logger.LogInformation("Valtuutus schema change: {Sql}", sql);

    /// <summary>
    /// Weasel continues the migration when this returns, so the failure is rethrown to stop the
    /// apply and surface it to the caller.
    /// </summary>
    public void OnFailure(DbCommand command, Exception ex)
    {
        logger.LogError(ex, "Valtuutus schema change failed: {Sql}", command.CommandText);
        ExceptionDispatchInfo.Capture(ex).Throw();
    }

    public void DestructiveChange(string description) =>
        logger.LogWarning("Valtuutus schema change recreates an object: {Description}", description);

    public void WithheldDrop(string description) =>
        logger.LogInformation("Valtuutus schema keeps an object the model does not declare: {Description}", description);
}
