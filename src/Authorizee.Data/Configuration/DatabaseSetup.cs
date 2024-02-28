using System.Data;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Data.Configuration;

public delegate IDbConnection DbConnectionFactory();

public static class DatabaseSetup
{
    public static IServiceCollection AddDatabaseSetup(this IServiceCollection  services, DbConnectionFactory factory, Action<IServiceCollection> configuring)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
        services.AddScoped<DbConnectionFactory>(_ => factory);
        configuring(services);
        return services;
    }
}