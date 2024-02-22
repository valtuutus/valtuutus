using Authorizee.Core.Schemas;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Core.Configuration;

public static class ConfigureSchema
{
    public static void AddSchemaConfiguration(this IServiceCollection services, Action<SchemaBuilder> config)
    {
        var builder = new SchemaBuilder();
        config(builder);
        var (schema, schemaGraph) = builder.Build();
        services.AddSingleton(schema);
        services.AddSingleton(schemaGraph);
        services.AddScoped<CheckEngine>();
        services.AddScoped<LookupEngine>();
        services.AddScoped<LookupEngine>();
    }
}