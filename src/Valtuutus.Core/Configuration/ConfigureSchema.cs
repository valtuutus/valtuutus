using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Lang.SchemaReaders;

namespace Valtuutus.Core.Configuration;

public static class ConfigureSchema
{
    /// <summary>
    /// Add Core Valtuutus services
    /// </summary>
    /// <param name="services">Service colletion</param>
    /// <param name="schemaText">Text representation of the schema</param>
    /// <returns></returns>
    public static IServiceCollection AddValtuutusCore(this IServiceCollection services, string schemaText)
    {
        var builder = new SchemaReader();
        var result = builder.Parse(schemaText);
        if (result.IsT1)
        {
            throw new InvalidOperationException(string.Join(",", result.AsT1.Select(x => x.ToString())));
        }
        var schema = result.AsT0;
        services.AddSingleton(schema);
        services.AddScoped<ICheckEngine, CheckEngine>();
        services.AddScoped<ILookupEntityEngine, LookupEntityEngine>();
        services.AddScoped<ILookupSubjectEngine,LookupSubjectEngine>();

        return services;
    }
    
    /// <summary>
    /// Add Core Valtuutus services
    /// </summary>
    /// <param name="services">Service colletion</param>
    /// <param name="stream">Stream of the representation of the schema</param>
    /// <returns></returns>
    public static IServiceCollection AddValtuutusCore(this IServiceCollection services, Stream stream)
    {
        var builder = new SchemaReader();
        var result = builder.Parse(stream);
        if (result.IsT1)
        {
            throw new InvalidOperationException(string.Join(",", result.AsT1.Select(x => x.ToString())));
        }
        var schema = result.AsT0;
        services.AddSingleton(schema);
        services.AddScoped<ICheckEngine, CheckEngine>();
        services.AddScoped<ILookupEntityEngine, LookupEntityEngine>();
        services.AddScoped<ILookupSubjectEngine,LookupSubjectEngine>();

        return services;
    }
    
}
