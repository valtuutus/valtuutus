using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Core.Schemas;

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
        // LookupSubject V2 plan rewriter for relational providers. Harmless when V2 is not opted in
        // (nothing resolves LookupSubjectPlanCache then); it reads the registered options and the
        // provider's ILookupSubjectBatchOps, and changes nothing when either says so.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILookupSubjectPlanRewriter, UsersetLevelsRewriter>());
        // LookupEntity V2 plan rewriter for relational providers. Harmless when V2 is not opted in
        // (nothing resolves LookupEntityPlanCache then); it reads the registered options and the
        // provider's ILookupEntityBatchOps, and changes nothing when either says so.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILookupEntityPlanRewriter, FixpointLevelsRewriter>());
        // V2 physical executor for relational providers: packs each wave into one DbBatch through
        // the provider's IRelationalBatchOps registration, resolved lazily (at the first executor
        // creation during a check, never at startup); a provider without one gets individual
        // dispatch. Replace so it wins whether AddValtuutusCore() ran before or after.
        services.Replace(ServiceDescriptor.Singleton<Func<Schema, IPhysicalExecutor>>(
            sp => schema => new BatchedPhysicalExecutor(schema, sp.GetService<IRelationalBatchOps>())));
        // LookupSubject V2 runner for relational providers: each step as one DbBatch through the
        // provider's IRelationalBatchOps and ILookupSubjectBatchOps registrations; a provider
        // without them gets one reader call per query. Harmless when V2 is not opted in (nothing
        // resolves ILookupSubjectQueryRunner then). Replace so it wins whether
        // AddValtuutusLookupSubjectV2() ran before or after.
        services.Replace(ServiceDescriptor.Scoped<ILookupSubjectQueryRunner>(sp =>
            new BatchedLookupSubjectQueryRunner(
                sp.GetRequiredService<IDataReaderProvider>(),
                sp.GetService<IRelationalBatchOps>(),
                sp.GetService<ILookupSubjectBatchOps>())));
        // LookupEntity V2 runner for relational providers: each step as one DbBatch through the
        // provider's IRelationalBatchOps and ILookupEntityBatchOps registrations; a provider without
        // them gets one reader call per query. Harmless when V2 is not opted in (nothing resolves
        // ILookupEntityQueryRunner then). Replace so it wins whether AddValtuutusLookupEntityV2() ran
        // before or after.
        services.Replace(ServiceDescriptor.Scoped<ILookupEntityQueryRunner>(sp =>
            new BatchedLookupEntityQueryRunner(
                sp.GetRequiredService<IDataReaderProvider>(),
                sp.GetService<IRelationalBatchOps>(),
                sp.GetService<ILookupEntityBatchOps>())));
        return services;
    }
}
