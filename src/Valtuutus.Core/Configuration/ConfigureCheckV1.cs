using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Core.Configuration;

public static class ConfigureCheckV1
{
    /// <summary>
    /// Opts out of the default plan/executor check engine and registers the legacy recursive
    /// check engine (CheckEngine). Call after <see cref="ConfigureSchema.AddValtuutusCore(IServiceCollection, string, IReadOnlyDictionary{string, Func{IDictionary{string, object?}, bool}}?)"/>
    /// and before <c>AddCaching</c> (Valtuutus.Data.Caching) if you use it.
    /// </summary>
    public static IServiceCollection AddValtuutusCheckV1(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<ICheckEngine, CheckEngine>());
        return services;
    }
}
