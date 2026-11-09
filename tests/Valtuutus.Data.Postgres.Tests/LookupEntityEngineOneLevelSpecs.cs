using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

// The shared LookupEntity specs with one hop per query, next to LookupEntityEngineSpecs on the
// provider default, so both shapes of the walk stay equal to the V1 engine.
public sealed class LookupEntityEngineOneLevelSpecs : BaseLookupEntityEngineSpecs, IClassFixture<PostgresFixture>
{
    public LookupEntityEngineOneLevelSpecs(PostgresFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            ((PostgresFixture)Fixture).Options with { LookupHopsPerQuery = 1 });
}
