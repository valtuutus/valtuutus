using Valtuutus.Core.Data;
using Microsoft.Extensions.DependencyInjection;
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
        builder.Services.AddSingleton(postgresOptions);
        builder.Services.AddScoped<IDataReaderProvider, PostgresDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, PostgresDataWriterProvider>();
        builder.Services.AddScoped<IDbDataWriterProvider, PostgresDataWriterProvider>();
        return builder;
    }

    /// <summary>
    /// Adds a YugabyteDB data reader and writer to the service collection. YugabyteDB is PostgreSQL-wire
    /// compatible, so this reuses the Postgres reader verbatim and swaps in a writer that avoids the binary
    /// <c>COPY</c> and <c>MERGE</c> statements YugabyteDB does not support (SQLSTATE 0A000).
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="factory">This is a scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <param name="options">Options to configure schema and table names.</param>
    public static IValtuutusDataBuilder AddYugabyte(this IServiceCollection services,
        Func<IServiceProvider, DbConnectionFactory> factory,
        ValtuutusPostgresOptions? options = null)
    {
        var postgresOptions = options ?? new ValtuutusPostgresOptions();
        var builder = services.AddValtuutusData();
        builder.Services.AddDbSetup(factory, postgresOptions);
        builder.Services.AddSingleton(postgresOptions);
        builder.Services.AddScoped<IDataReaderProvider, PostgresDataReaderProvider>();
        builder.Services.AddScoped<IDataWriterProvider, YugabyteDataWriterProvider>();
        builder.Services.AddScoped<IDbDataWriterProvider, YugabyteDataWriterProvider>();
        return builder;
    }
}
