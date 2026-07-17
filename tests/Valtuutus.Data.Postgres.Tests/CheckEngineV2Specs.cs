using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class CheckEngineV2Specs : BaseCheckEngineSpecs
{
    public CheckEngineV2Specs(PostgresFixture fixture): base(fixture){}
    protected override bool UseCheckV2 => true;

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddPostgres(_ =>  ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
}
