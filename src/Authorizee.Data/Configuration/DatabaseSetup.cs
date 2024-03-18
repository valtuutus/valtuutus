using System.Data;
using Dapper;
using IdGen.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Sqids;

namespace Authorizee.Data.Configuration;

public delegate IDbConnection DbConnectionFactory();

public static class DatabaseSetup
{
    public static IServiceCollection AddDatabaseSetup(this IServiceCollection  services, DbConnectionFactory factory, Action<IServiceCollection> configuring)
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