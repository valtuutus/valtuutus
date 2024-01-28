using Authorizee.Core.Schemas;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Core.Configuration;

public static class ConfigureSchema
{
    public static void AddSchemaConfiguration(this IServiceCollection services, Action<SchemaBuilder> config)
    {
        var builder = new SchemaBuilder();
        config(builder);
        services.AddSingleton(builder.Build());
    }
}