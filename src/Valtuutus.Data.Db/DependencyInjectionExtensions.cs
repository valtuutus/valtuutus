using Dapper;
using Microsoft.Extensions.DependencyInjection;

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
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new JsonTypeHandler());
        SqlMapper.AddTypeHandler(new UlidTypeHandler());
        services.AddSingleton(options);
        return services;
    }
}