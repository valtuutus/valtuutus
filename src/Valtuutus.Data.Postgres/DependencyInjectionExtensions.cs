using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Valtuutus.Data.Postgres;

public static class DependencyInjectionExtensions
{
    public static void AddPostgres(this IServiceCollection services)
    {
        services.AddScoped<IDataReaderProvider, PostgresDataReaderProvider>();
        services.AddScoped<IDataWriterProvider, PostgresDataWriterProvider>();
    }
    
}