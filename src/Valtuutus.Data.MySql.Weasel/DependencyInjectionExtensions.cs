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
        builder.Services.AddSingleton<MySqlSchemaApplier>();
        builder.Services.AddSingleton<ISchemaApplier>(sp => sp.GetRequiredService<MySqlSchemaApplier>());
        builder.Services.AddHostedService(sp => new SchemaHostedService(sp.GetRequiredService<MySqlSchemaApplier>()));
        return builder;
    }
}
