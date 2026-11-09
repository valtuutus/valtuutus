using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Weasel;

namespace Valtuutus.Data.Postgres.Weasel;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Creates and migrates the Valtuutus tables with Weasel when the host starts, under an
    /// advisory lock so nodes starting together take turns.
    /// </summary>
    public static IValtuutusDataBuilder AddPostgresWeasel(this IValtuutusDataBuilder builder,
        Action<ValtuutusPostgresWeaselOptions>? configure = null)
    {
        var options = new ValtuutusPostgresWeaselOptions();
        configure?.Invoke(options);
        AutoCreateGuard.EnsureSafe(options.AutoCreate);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISchemaApplier, PostgresSchemaApplier>();
        builder.Services.AddHostedService<SchemaHostedService>();
        return builder;
    }

    /// <summary>Applies the Valtuutus schema now, with the options given to AddPostgresWeasel.</summary>
    public static Task ApplyValtuutusSchemaAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var applier = services.GetService<ISchemaApplier>()
            ?? throw new InvalidOperationException("Call AddPostgresWeasel() on the Valtuutus data builder before ApplyValtuutusSchemaAsync().");
        return applier.ApplyAsync(cancellationToken);
    }
}
