using Authorizee.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Data.Postgres;

public static class DependencyInjectionExtensions
{
    public static void AddPostgres(this IServiceCollection services)
    {
        services.AddScoped<IDataReaderProvider, PostgresDataReaderProvider>();
        services.AddScoped<IDataWriterProvider, PostgresDataWriterProvider>();
    }
    
}