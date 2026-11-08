namespace Valtuutus.Data.Weasel;

/// <summary>Brings the Valtuutus tables up to date with the provider's model.</summary>
internal interface ISchemaApplier
{
    Task ApplyAsync(CancellationToken cancellationToken);
}
