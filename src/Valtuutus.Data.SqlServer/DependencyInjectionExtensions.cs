using Dapper;
using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Valtuutus.Data.SqlServer;

public static class DependencyInjectionExtensions
{
    
    /// <summary>
    /// Adds SqlServer data reader and writer to the service collection
    /// </summary>
    /// <param name="builder">Valtuutus data builder</param>
    /// <param name="factory">This is a scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddSqlServer(this IValtuutusDataBuilder builder, Func<IServiceProvider, DbConnectionFactory> factory)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        builder.Services.AddScoped(factory);
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
        builder.Services.AddScoped<IDataReaderProvider, SqlServerDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, SqlServerDataWriterProvider>();

        return builder;
    }
    
}