using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Valtuutus.Data.Configuration;

namespace Valtuutus.Data.Postgres;

public static class DependencyInjectionExtensions
{
    public static void AddPostgres(this IServiceCollection services, int maxConcurrentQueries = 5)
    {
        services.AddScoped<IDataReaderProvider>(sp => new PostgresDataReaderProvider(sp.GetRequiredService<DbConnectionFactory>(), sp.GetRequiredService<ILogger<IDataReaderProvider>>(), maxConcurrentQueries));
        services.AddScoped<IDataWriterProvider, PostgresDataWriterProvider>();
    }
    
}