using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Engines.LookupSubject.V2;

namespace Valtuutus.Core.Configuration;

public static class ConfigureLookupSubjectV2
{
    /// <summary>
    /// Opt-in: replaces the recursive lookup-subject engine with the graph/step engine
    /// (LookupSubjectEngineV2). Call after <c>AddValtuutusCore</c>, and before <c>AddCaching</c>
    /// (Valtuutus.Data.Caching) if you use it, since it replaces the registered
    /// <c>ILookupSubjectEngine</c> outright.
    /// </summary>
    public static IServiceCollection AddValtuutusLookupSubjectV2(this IServiceCollection services)
    {
        services.TryAddSingleton<LookupSubjectPlanCache>();
        services.TryAddScoped<ILookupSubjectQueryRunner, DefaultLookupSubjectQueryRunner>();
        services.Replace(ServiceDescriptor.Scoped<ILookupSubjectEngine, LookupSubjectEngineV2>());
        return services;
    }
}
