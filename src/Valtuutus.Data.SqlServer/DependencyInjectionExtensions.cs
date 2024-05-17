using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Valtuutus.Data.Configuration;

namespace Valtuutus.Data.SqlServer;

public static class DependencyInjectionExtensions
{
    public static void AddSqlServer(this IServiceCollection services, int maxConcurrentQueries = 5)
    {
        services.AddScoped<IDataReaderProvider>(sp => new SqlServerDataReaderProvider(sp.GetRequiredService<DbConnectionFactory>(), sp.GetRequiredService<ILogger<SqlServerDataReaderProvider>>(),maxConcurrentQueries));
        services.AddScoped<IDataWriterProvider, SqlServerDataWriterProvider>();

    }
    
}