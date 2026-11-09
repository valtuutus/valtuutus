using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

public sealed class CheckEngineSpecs : BaseCheckEngineSpecs, IClassFixture<PostgresFixture>
{
    public CheckEngineSpecs(PostgresFixture fixture): base(fixture){}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddPostgres(_ =>  ((IWithDbConnectionFactory)Fixture).DbFactory);
    }
}