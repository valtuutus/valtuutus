using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Authorizee.Data.Configuration;

public static class DatabaseSetup
{
    public static void AddDatabaseSetup(this IServiceCollection  services, string connectionString)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        services.AddTransient<IDbConnection>(_ => new NpgsqlConnection(connectionString));
    }
}