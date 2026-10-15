using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

// The shared LookupSubject specs with one userset level per query, next to LookupSubjectEngineSpecs
// on the provider default, so both shapes of the plan stay equal to the V1 engine.
[Collection("PostgreSqlSpec")]
public sealed class LookupSubjectEngineOneLevelSpecs : BaseLookupSubjectEngineSpecs
{
    public LookupSubjectEngineOneLevelSpecs(PostgresFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            new ValtuutusPostgresOptions("public", "transactions", "relation_tuples", "attributes") { LookupHopsPerQuery = 1 });
}
