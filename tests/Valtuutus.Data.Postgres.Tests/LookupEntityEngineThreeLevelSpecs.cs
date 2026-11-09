using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

// The shared LookupEntity specs with three hops per query, so the deeper level statements stay
// equal to the V1 engine on joined grants, shortcuts and cycles.
public sealed class LookupEntityEngineThreeLevelSpecs : BaseLookupEntityEngineSpecs, IClassFixture<PostgresFixture>
{
    public LookupEntityEngineThreeLevelSpecs(PostgresFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            ((PostgresFixture)Fixture).Options with { LookupHopsPerQuery = 3 });
}
