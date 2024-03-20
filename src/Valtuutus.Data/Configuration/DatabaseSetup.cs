using System.Data;
using Dapper;
using IdGen.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Sqids;

namespace Valtuutus.Data.Configuration;

public delegate IDbConnection DbConnectionFactory();

public static class DatabaseSetup
{
    /// <summary>
    /// Add Valtuutus database services
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="factory">This is a scoped connection factory. Can be used to set multitenant access to the database.</param>
    /// <param name="configuring">Aditional configuration</param>
    /// <returns></returns>
    public static IServiceCollection AddValtuutusDatabase(this IServiceCollection  services, DbConnectionFactory factory, Action<IServiceCollection> configuring)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
        services.AddScoped<DbConnectionFactory>(_ => factory);
        services.AddSingleton<SqidsEncoder<long>>();
        services.AddIdGen(1);

        configuring(services);
        return services;
    }
}