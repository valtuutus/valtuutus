using Valtuutus.Data.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class CheckEngineSpecs : DataCheckEngineSpecs
{
    public CheckEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void AddSpecificProvider(IServiceCollection services)
    {
        services.AddPostgres();
    }
}