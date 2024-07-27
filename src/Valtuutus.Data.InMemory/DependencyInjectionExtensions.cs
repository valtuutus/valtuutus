using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds InMemory data reader and writer to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddInMemory(this IServiceCollection services)
    {
        var builder = services.AddValtuutusData();
        builder.Services.AddSingleton<InMemoryController>();
        builder.Services.AddScoped<IDataReaderProvider, InMemoryProvider>();
        builder.Services.AddScoped<IDataWriterProvider, InMemoryProvider>();
        return builder;
    }
}