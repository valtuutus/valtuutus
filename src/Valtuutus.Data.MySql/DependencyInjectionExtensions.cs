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
        builder.Services.AddSingleton(options);
        builder.Services.AddScoped<IDataReaderProvider, MySqlDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, MySqlDataWriterProvider>();
        builder.Services.AddScoped<IDbDataWriterProvider, MySqlDataWriterProvider>();
        builder.Services.AddScoped<ITombstoneReaperProvider, MySqlTombstoneReaperProvider>();
        return builder;
    }
}
