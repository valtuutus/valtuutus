using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class CheckEngineExplainSpecs : BaseCheckEngineV1OnlyExplainSpecs
{
    public CheckEngineExplainSpecs(PostgresFixture fixture) : base(fixture) { }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory);
}
