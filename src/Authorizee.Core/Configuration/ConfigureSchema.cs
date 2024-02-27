using Authorizee.Core.Schemas;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Core.Configuration;

public static class ConfigureSchema
{
    public static IServiceCollection AddSchemaConfiguration(this IServiceCollection services, Action<SchemaBuilder> config)
    {
        var builder = new SchemaBuilder();
        config(builder);
        var schema = builder.Build();
        services.AddSingleton(schema);
        services.AddScoped<CheckEngine>();
        services.AddScoped<LookupEntityEngine>();
        services.AddScoped<LookupSubjectEngine>();

        return services;
    }
}