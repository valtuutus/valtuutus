using Valtuutus.Data.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Valtuutus.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class LookupEntityEngineSpecs : DataLookupEntityEngineSpecs
{

    public LookupEntityEngineSpecs(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void AddSpecificProvider(IServiceCollection services)
    {
        services.AddSqlServer();
    }
}