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
        builder.Services.AddSingleton<SqlServerSchemaApplier>();
        builder.Services.AddSingleton<ISchemaApplier>(sp => sp.GetRequiredService<SqlServerSchemaApplier>());
        builder.Services.AddHostedService(sp => new SchemaHostedService(sp.GetRequiredService<SqlServerSchemaApplier>()));
        return builder;
    }
}
