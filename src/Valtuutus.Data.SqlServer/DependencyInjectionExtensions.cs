using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Schemas;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds SqlServer data reader and writer to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="factory">This is a scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <param name="options">Options to configure schema and table names.</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddSqlServer(this IServiceCollection services,
        Func<IServiceProvider, DbConnectionFactory> factory,
        ValtuutusSqlServerOptions? options = null)
    {
        var sqlServerOptions = options ?? new ValtuutusSqlServerOptions();
        var builder = services.AddValtuutusData();
        builder.Services.AddDbSetup(factory, sqlServerOptions);
        // Singleton batch ops for BatchedPhysicalExecutor. Constructed lazily — the factory only
        // runs if something resolves IRelationalBatchOps (i.e. CheckV2 is opted in).
        //
        // DbConnectionFactory is Scoped (AddDbSetup), but this factory runs against the ROOT
        // provider (singleton factories always resolve from root, by design — never the calling
        // request's scope). Resolving a Scoped service directly from root either throws under
        // ServiceProviderOptions.ValidateScopes = true, or silently succeeds against root's own
        // implicit scope. An earlier version of this method sidestepped the whole problem by
        // registering batchOps: null unconditionally (SqlServer's IRelationalBatchOps used to live
        // on the scoped SqlServerDataReaderProvider, so there was no singleton-safe way to obtain
        // it) — degrading every op to individual dispatch via PhysicalOpRunner. Now that batch ops
        // live on the standalone SqlServerBatchOps (which only needs a DbConnectionFactory, not a
        // scoped reader instance), a disposed-immediately child scope resolves that factory
        // correctly: still only ever probes once (this factory itself only runs once, at first
        // IRelationalBatchOps resolution), just through a scope that's valid to resolve Scoped
        // services from — same pattern AddPostgres uses for PostgresBatchOps.
        builder.Services.AddSingleton<IRelationalBatchOps>(sp =>
        {
            using var scope = sp.CreateScope();
            return new SqlServerBatchOps(
                scope.ServiceProvider.GetRequiredService<DbConnectionFactory>(),
                sp.GetRequiredService<ValtuutusDataOptions>(),
                sqlServerOptions);
        });
        // Harmless when CheckV2 isn't opted in (nothing resolves CheckPlanExecutorPool then).
        // Replace (not TryAdd) so this always wins regardless of whether AddValtuutusCheckV2()
        // ran before or after this call. The batch capability is injected (resolved inside the
        // inner lambda, i.e. at first executor creation during a check — never at startup), not
        // discovered by type-testing the reader; GetService (not Required) so removing the
        // IRelationalBatchOps registration degrades the executor to individual dispatch.
        builder.Services.Replace(ServiceDescriptor.Singleton<Func<Schema, IPhysicalExecutor>>(
            sp => schema => new BatchedPhysicalExecutor(schema, sp.GetService<IRelationalBatchOps>())));
        builder.Services.AddScoped<IDataReaderProvider, SqlServerDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, SqlServerDataWriterProvider>();
        builder.Services.AddScoped<IDbDataWriterProvider, SqlServerDataWriterProvider>();
        return builder;
    }
}
