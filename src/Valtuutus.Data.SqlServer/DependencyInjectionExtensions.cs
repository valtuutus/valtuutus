using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Valtuutus.Data.SqlServer;

public static class DependencyInjectionExtensions
{
    public static void AddSqlServer(this IServiceCollection services)
    {
        services.AddScoped<IDataReaderProvider, SqlServerDataReaderProvider>();
        services.AddScoped<IDataWriterProvider, SqlServerDataWriterProvider>();

    }
    
}