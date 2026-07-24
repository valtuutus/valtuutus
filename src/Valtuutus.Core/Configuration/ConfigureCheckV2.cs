using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Configuration;

public static class ConfigureCheckV2
{
    /// <summary>
    /// Opt-in: replaces the recursive check engine with the plan/executor engine (CheckEngineV2).
    /// Call after <see cref="ConfigureSchema.AddValtuutusCore(IServiceCollection, string, IReadOnlyDictionary{string, Func{IDictionary{string, object?}, bool}}?)"/>,
    /// and before <c>AddCaching</c> (Valtuutus.Data.Caching) if you use it, since it replaces the registered <c>ICheckEngine</c> outright.
    /// </summary>
    public static IServiceCollection AddValtuutusCheckV2(this IServiceCollection services)
    {
        services.TryAddSingleton<CheckPlanCache>();
        // Default: the generic, provider-agnostic executor. A relational provider (e.g.
        // Valtuutus.Data.Postgres) may Replace() this with a batching factory; order between
        // AddValtuutusCheckV2() and the provider's AddXxx() doesn't matter — TryAddSingleton here
        // only wins if nothing else has registered first, and Replace() always wins regardless
        // of call order.
        services.TryAddSingleton<Func<Schema, IPhysicalExecutor>>(_ => static schema => new DefaultPhysicalExecutor(schema));
        services.TryAddSingleton<CheckPlanExecutorPool>();
        services.Replace(ServiceDescriptor.Scoped<ICheckEngine, CheckEngineV2>());
        return services;
    }
}
