using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Weasel;

namespace Valtuutus.Data.SqlServer.Weasel;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Creates and migrates the Valtuutus tables with Weasel when the host starts, under an
    /// application lock so nodes starting together take turns.
    /// </summary>
    public static IValtuutusDataBuilder AddSqlServerWeasel(this IValtuutusDataBuilder builder,
        Action<ValtuutusSqlServerWeaselOptions>? configure = null)
    {
        var options = new ValtuutusSqlServerWeaselOptions();
        configure?.Invoke(options);
        AutoCreateGuard.EnsureSafe(options.AutoCreate);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ISchemaApplier, SqlServerSchemaApplier>();
        builder.Services.AddHostedService<SchemaHostedService>();
        return builder;
    }

    /// <summary>Applies the Valtuutus schema now, with the options given to AddSqlServerWeasel.</summary>
    public static Task ApplyValtuutusSchemaAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var applier = services.GetService<ISchemaApplier>()
            ?? throw new InvalidOperationException("Call AddSqlServerWeasel() on the Valtuutus data builder before ApplyValtuutusSchemaAsync().");
        return applier.ApplyAsync(cancellationToken);
    }
}
