using Dapper;
using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
namespace Valtuutus.Data.Postgres;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds Postgres data reader and writer to the service collection
    /// </summary>
    /// <param name="builder">Valtuutus data builder</param>
    /// <param name="factory">This is a scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddPostgres(this IValtuutusDataBuilder builder, Func<IServiceProvider, DbConnectionFactory> factory)
    {
        builder.Services.AddScoped(factory);
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
        builder.Services.AddScoped<IDataReaderProvider, PostgresDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, PostgresDataWriterProvider>();
        return builder;
    }
}