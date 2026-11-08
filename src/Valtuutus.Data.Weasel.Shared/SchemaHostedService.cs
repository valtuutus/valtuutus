using Microsoft.Extensions.Hosting;

namespace Valtuutus.Data.Weasel;

/// <summary>
/// Applies the schema while the host starts, so the application only serves once the tables are
/// current. A failure propagates and stops the host.
/// </summary>
internal sealed class SchemaHostedService(ISchemaApplier applier) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => applier.ApplyAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
