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
        var builder = services.AddValtuutusData();
        builder.Services.AddDbSetup(factory, options ?? new ValtuutusSqlServerOptions());
        // Harmless when CheckV2 isn't opted in (nothing resolves CheckPlanExecutorPool then).
        // Replace (not TryAdd) so this always wins regardless of whether AddValtuutusCheckV2()
        // ran before or after this call — same reasoning as AddPostgres's identical registration.
        builder.Services.Replace(ServiceDescriptor.Singleton<Func<Schema, IPhysicalExecutor>>(
            static _ => static schema => new BatchedPhysicalExecutor(schema)));
        builder.Services.AddScoped<IDataReaderProvider, SqlServerDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, SqlServerDataWriterProvider>();
        builder.Services.AddScoped<IDbDataWriterProvider, SqlServerDataWriterProvider>();
        return builder;
    }
}
