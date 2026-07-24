using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Schemas;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

public static class DependencyInjectionExtensions
{
    /// <summary>
    /// Adds Postgres data reader and writer to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="factory">This is a scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <param name="options">Options to configure schema and table names.</param>
    /// <returns></returns>
    public static IValtuutusDataBuilder AddPostgres(this IServiceCollection services,
        Func<IServiceProvider, DbConnectionFactory> factory,
        ValtuutusPostgresOptions? options = null)
    {
        var postgresOptions = options ?? new ValtuutusPostgresOptions();
        var builder = services.AddValtuutusData();
        builder.Services.AddDbSetup(factory, postgresOptions);
        // Singleton batch ops for BatchedPhysicalExecutor: NpgsqlDataSource-backed (the same
        // cached data source the scoped readers share), so nothing per-scope is captured beyond
        // the connection string the first resolution probes. Constructed lazily — the factory only
        // runs if something resolves IRelationalBatchOps (i.e. CheckV2 is opted in).
        //
        // DbConnectionFactory is Scoped (AddDbSetup), but this factory runs against the ROOT
        // provider (singleton factories always resolve from root, by design — never the calling
        // request's scope). Resolving a Scoped service directly from root either throws under
        // ServiceProviderOptions.ValidateScopes = true, or silently succeeds against root's own
        // implicit scope — same bug class as the one fixed in AddSqlServer's executor factory,
        // just one layer deeper. A disposed-immediately child scope sidesteps both: still only
        // ever probes once (this factory itself only runs once, at first IRelationalBatchOps
        // resolution), just through a scope that's valid to resolve Scoped services from.
        builder.Services.AddSingleton<IRelationalBatchOps>(sp =>
        {
            using var scope = sp.CreateScope();
            return new PostgresBatchOps(
                scope.ServiceProvider.GetRequiredService<DbConnectionFactory>(),
                sp.GetRequiredService<ValtuutusDataOptions>(),
                postgresOptions);
        });
        // Harmless when CheckV2 isn't opted in (nothing resolves CheckPlanExecutorPool then).
        // Replace (not TryAdd) so this always wins regardless of whether AddValtuutusCheckV2()
        // ran before or after this call. The batch capability is injected (resolved inside the
        // inner lambda, i.e. at first executor creation during a check — never at startup), not
        // discovered by type-testing the reader; GetService (not Required) so removing the
        // IRelationalBatchOps registration degrades the executor to individual dispatch.
        builder.Services.Replace(ServiceDescriptor.Singleton<Func<Schema, IPhysicalExecutor>>(
            sp => schema => new BatchedPhysicalExecutor(schema, sp.GetService<IRelationalBatchOps>())));
        builder.Services.AddSingleton(postgresOptions);
        builder.Services.AddScoped<IDataReaderProvider, PostgresDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, PostgresDataWriterProvider>();
        builder.Services.AddScoped<IDbDataWriterProvider, PostgresDataWriterProvider>();
        return builder;
    }
}
