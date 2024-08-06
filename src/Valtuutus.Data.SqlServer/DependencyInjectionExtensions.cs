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
    /// <returns></returns>
    public static IValtuutusDataBuilder AddSqlServer(this IServiceCollection services, Func<IServiceProvider, DbConnectionFactory> factory)
    {
        var builder = services.AddValtuutusData();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        builder.Services.AddScoped(factory);
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
        SqlMapper.AddTypeHandler(new UlidTypeHandler());
        builder.Services.AddScoped<IDataReaderProvider, SqlServerDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, SqlServerDataWriterProvider>();

        return builder;
    }
    
}