using Dapper;
using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds SqlServer data reader and writer to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="factory">This is a scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <param name="options">Options to configure schema and table names.</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddSqlServer(this IServiceCollection services,
        Func<IServiceProvider, DbConnectionFactory> factory,
        ValtuutusSqlServerOptions? options = null)
    {
        var builder = services.AddValtuutusData();
        builder.Services.AddDbSetup(factory, options ?? new ValtuutusSqlServerOptions());
        builder.Services.AddScoped<IDataReaderProvider, SqlServerDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, SqlServerDataWriterProvider>();

        return builder;
    }
    
}