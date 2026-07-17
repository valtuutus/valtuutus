using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Core.Configuration;

public static class ConfigureCheckV2
{
    /// <summary>
    /// Opt-in: replaces the recursive check engine with the plan/executor engine (CheckEngineV2).
    /// Call after <see cref="ConfigureSchema.AddValtuutusCore(IServiceCollection, string, IReadOnlyDictionary{string, Func{IDictionary{string, object?}, bool}}?)"/>.
    /// Explain requests are still served by the V1 engine.
    /// </summary>
    public static IServiceCollection AddValtuutusCheckV2(this IServiceCollection services)
    {
        services.TryAddSingleton<CheckPlanCache>();
        services.TryAddSingleton<CheckPlanExecutorPool>();
        services.Replace(ServiceDescriptor.Scoped<ICheckEngine, CheckEngineV2>());
        return services;
    }
}
