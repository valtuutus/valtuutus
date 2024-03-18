using Authorizee.Data.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Authorizee.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class CheckEngineSpecs : DataCheckEngineSpecs
{
    public CheckEngineSpecs(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void AddSpecificProvider(IServiceCollection services)
    {
        services.AddSqlServer();
    }
}