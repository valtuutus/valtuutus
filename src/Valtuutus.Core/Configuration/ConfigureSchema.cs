using Valtuutus.Core.Schemas;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;

namespace Valtuutus.Core.Configuration;

public static class ConfigureSchema
{
    /// <summary>
    /// Add Core Valtuutus services
    /// </summary>
    /// <param name="services">Service colletion</param>
    /// <param name="config">Action to configure the schema graph</param>
    /// <returns></returns>
    public static IServiceCollection AddValtuutusCore(this IServiceCollection services, Action<SchemaBuilder> config)
    {
        var builder = new SchemaBuilder();
        config(builder);
        var schema = builder.Build();
        services.AddSingleton(schema);
        services.AddScoped<CheckEngine>();
        services.AddScoped<LookupEntityEngine>();
        services.AddScoped<LookupSubjectEngine>();
        services.AddScoped<DataEngine>();

        return services;
    }
}