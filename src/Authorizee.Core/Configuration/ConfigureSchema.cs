using Authorizee.Core.Schemas;
using Jint;
using Jint.Native;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Core.Configuration;

public static class ConfigureSchema
{
    public static void AddSchemaConfiguration(this IServiceCollection services, Action<SchemaBuilder> config)
    {
        var builder = new SchemaBuilder();
        config(builder);
        services.AddSingleton(builder.Build());
        services.AddSingleton<PermissionEngine>();
        services.AddSingleton<LookupEngine>();
    }
}