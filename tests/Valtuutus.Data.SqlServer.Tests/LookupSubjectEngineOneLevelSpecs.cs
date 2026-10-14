using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

// The shared LookupSubject specs with one userset level per query, next to LookupSubjectEngineSpecs
// on the provider default, so both shapes of the plan stay equal to the V1 engine.
[Collection("SqlServerSpec")]
public sealed class LookupSubjectEngineOneLevelSpecs : BaseLookupSubjectEngineSpecs
{
    public LookupSubjectEngineOneLevelSpecs(SqlServerFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            new ValtuutusSqlServerOptions("dbo", "transactions", "relation_tuples", "attributes") { LookupHopsPerQuery = 1 });
}
