using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds the MySQL/MariaDB data reader and writer to the service collection.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="factory">Scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <param name="options">Server type, database and table names.</param>
    public static IValtuutusDataBuilder AddMySql(this IServiceCollection services,
        Func<IServiceProvider, DbConnectionFactory> factory,
        ValtuutusMySqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = services.AddValtuutusData();
        builder.Services.AddDbSetup(factory, options);
        // Singleton batch ops for BatchedPhysicalExecutor: they run on the cached data source the
        // scoped readers share, so nothing per scope is captured beyond the connection string the
        // first resolution probes. The factory runs only if something resolves IRelationalBatchOps.
        //
        // DbConnectionFactory is scoped (AddDbSetup), but a singleton factory resolves from the root
        // provider. Resolving a scoped service from root either throws under
        // ServiceProviderOptions.ValidateScopes or silently resolves it in root's own implicit scope.
        // A child scope disposed right away is valid to resolve scoped services from, and the probe
        // still runs once.
        builder.Services.AddSingleton<IRelationalBatchOps>(sp =>
        {
            using var scope = sp.CreateScope();
            return new MySqlBatchOps(
                scope.ServiceProvider.GetRequiredService<DbConnectionFactory>(),
                sp.GetRequiredService<ValtuutusDataOptions>(),
                options);
        });
        builder.Services.AddSingleton<ILookupSubjectBatchOps>(new MySqlLookupSubjectBatchOps(options));
        builder.Services.AddSingleton<ILookupEntityBatchOps>(new MySqlLookupEntityBatchOps(options));
        builder.Services.AddSingleton(options);
        builder.Services.AddScoped<IDataReaderProvider, MySqlDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, MySqlDataWriterProvider>();
        builder.Services.AddScoped<IDbDataWriterProvider, MySqlDataWriterProvider>();
        builder.Services.AddScoped<ITombstoneReaperProvider, MySqlTombstoneReaperProvider>();
        return builder;
    }
}
