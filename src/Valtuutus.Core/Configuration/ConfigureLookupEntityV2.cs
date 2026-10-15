using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupEntity.V2;

namespace Valtuutus.Core.Configuration;

public static class ConfigureLookupEntityV2
{
    /// <summary>
    /// Opt-in: replaces the recursive lookup-entity engine with the graph/step engine
    /// (LookupEntityEngineV2). Call after <c>AddValtuutusCore</c>, and before <c>AddCaching</c>
    /// (Valtuutus.Data.Caching) if you use it, since it replaces the registered
    /// <c>ILookupEntityEngine</c> outright.
    /// </summary>
    public static IServiceCollection AddValtuutusLookupEntityV2(this IServiceCollection services)
    {
        services.TryAddSingleton<LookupEntityPlanCache>();
        services.TryAddScoped<ILookupEntityQueryRunner, DefaultLookupEntityQueryRunner>();
        services.Replace(ServiceDescriptor.Scoped<ILookupEntityEngine, LookupEntityEngineV2>());
        return services;
    }
}
