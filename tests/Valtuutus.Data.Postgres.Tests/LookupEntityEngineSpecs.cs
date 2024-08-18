using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class LookupEntityEngineSpecs : BaseLookupEntityEngineSpecs
{
    public LookupEntityEngineSpecs(PostgresFixture fixture) : base(fixture){}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddPostgres(_ =>  ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
}