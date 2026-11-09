namespace Valtuutus.Data;

/// <summary>
/// Brings the Valtuutus tables up to date with a provider's model. Registered by a provider's
/// schema package (AddPostgresWeasel, AddSqlServerWeasel, AddMySqlWeasel) and run by
/// <see cref="DependencyInjectionExtensions.ApplyValtuutusSchemaAsync"/>.
/// </summary>
public interface ISchemaApplier
{
    Task ApplyAsync(CancellationToken cancellationToken);
}
