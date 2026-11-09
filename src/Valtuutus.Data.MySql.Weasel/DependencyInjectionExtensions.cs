using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Weasel;

namespace Valtuutus.Data.MySql.Weasel;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Creates and migrates the Valtuutus tables with Weasel when the host starts, under a named
    /// lock so nodes starting together take turns.
    /// </summary>
    public static IValtuutusDataBuilder AddMySqlWeasel(this IValtuutusDataBuilder builder,
        Action<ValtuutusMySqlWeaselOptions>? configure = null)
    {
        var options = new ValtuutusMySqlWeaselOptions();
        configure?.Invoke(options);
        AutoCreateGuard.EnsureSafe(options.AutoCreate);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISchemaApplier, MySqlSchemaApplier>();
        builder.Services.AddHostedService<SchemaHostedService>();
        return builder;
    }

    /// <summary>Applies the Valtuutus schema now, with the options given to AddMySqlWeasel.</summary>
    public static Task ApplyValtuutusSchemaAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var applier = services.GetService<ISchemaApplier>()
            ?? throw new InvalidOperationException("Call AddMySqlWeasel() on the Valtuutus data builder before ApplyValtuutusSchemaAsync().");
        return applier.ApplyAsync(cancellationToken);
    }
}
