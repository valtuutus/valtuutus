using Authorizee.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Data.SqlServer;

public static class DependencyInjectionExtensions
{
    public static void AddSqlServer(this IServiceCollection services)
    {
        services.AddScoped<IRelationTupleReader, SqlServerRelationTupleReader>();
        services.AddScoped<IAttributeReader, SqlServerAttributeReader>();
    }
    
}