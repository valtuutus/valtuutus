using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.Db;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Helper to unify setting up db providers
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="factory">Connection factory</param>
    /// <param name="options">Options to control schema and table names</param>
    /// <returns></returns>
    public static IServiceCollection AddDbSetup(this IServiceCollection services,
        Func<IServiceProvider, DbConnectionFactory> factory,
        IValtuutusDbOptions options)
    {
        services.AddScoped(factory);
        services.AddSingleton(options);
        // V2 plan rewriter for relational providers. Harmless when V2 is not opted in (nothing
        // resolves IPlanRewriter then); TryAddEnumerable keeps repeated AddDbSetup calls
        // idempotent. Requires the registered IDataReaderProvider to implement
        // IRelationalCheckOps — both in-repo relational providers do.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlanRewriter, RelationalPlanRewriter>());
        return services;
    }
}
