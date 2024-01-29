using System.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Data.Configuration;

public delegate IDbConnection DbConnectionFactory();

public static class DatabaseSetup
{
    public static void AddDatabaseSetup(this IServiceCollection  services, DbConnectionFactory factory)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        services.AddSingleton<DbConnectionFactory>(_ => factory);
    }
}