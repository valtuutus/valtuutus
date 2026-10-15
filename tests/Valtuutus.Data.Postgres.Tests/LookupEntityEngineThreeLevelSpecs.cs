using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

// The shared LookupEntity specs with three hops per query, so the deeper level statements stay
// equal to the V1 engine on joined grants, shortcuts and cycles.
[Collection("PostgreSqlSpec")]
public sealed class LookupEntityEngineThreeLevelSpecs : BaseLookupEntityEngineSpecs
{
    public LookupEntityEngineThreeLevelSpecs(PostgresFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            new ValtuutusPostgresOptions("public", "transactions", "relation_tuples", "attributes") { LookupHopsPerQuery = 3 });
}
