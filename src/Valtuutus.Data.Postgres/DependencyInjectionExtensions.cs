using Dapper;
using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds Postgres data reader and writer to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="factory">This is a scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddPostgres(this IServiceCollection services, Func<IServiceProvider, DbConnectionFactory> factory)
    {
        var builder = services.AddValtuutusData();
        builder.Services.AddScoped(factory);
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
        SqlMapper.AddTypeHandler(new UlidTypeHandler());
        builder.Services.AddScoped<IDataReaderProvider, PostgresDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, PostgresDataWriterProvider>();
        return builder;
    }
}