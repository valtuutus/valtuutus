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
        builder.Services.AddSingleton<PostgresSchemaApplier>();
        builder.Services.AddSingleton<ISchemaApplier>(sp => sp.GetRequiredService<PostgresSchemaApplier>());
        builder.Services.AddHostedService(sp => new SchemaHostedService(sp.GetRequiredService<PostgresSchemaApplier>()));
        return builder;
    }
}
