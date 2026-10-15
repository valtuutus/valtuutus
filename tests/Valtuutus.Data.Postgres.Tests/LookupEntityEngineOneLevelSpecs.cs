using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

// The shared LookupEntity specs with one hop per query, next to LookupEntityEngineSpecs on the
// provider default, so both shapes of the walk stay equal to the V1 engine.
[Collection("PostgreSqlSpec")]
public sealed class LookupEntityEngineOneLevelSpecs : BaseLookupEntityEngineSpecs
{
    public LookupEntityEngineOneLevelSpecs(PostgresFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            new ValtuutusPostgresOptions("public", "transactions", "relation_tuples", "attributes") { LookupHopsPerQuery = 1 });
}
