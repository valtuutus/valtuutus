using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class LookupEntityEngineSpecs : DataLookupEntityEngineSpecs
{
    public LookupEntityEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddPostgres(_ =>  ((IWithDbConnectionFactory)_fixture).DbFactory);
    }
}