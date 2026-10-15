using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

// The shared LookupEntity specs with one hop per query, next to LookupEntityEngineSpecs on the
// provider default, so both shapes of the walk stay equal to the V1 engine.
[Collection("SqlServerSpec")]
public sealed class LookupEntityEngineOneLevelSpecs : BaseLookupEntityEngineSpecs
{
    public LookupEntityEngineOneLevelSpecs(SqlServerFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            new ValtuutusSqlServerOptions("dbo", "transactions", "relation_tuples", "attributes") { LookupHopsPerQuery = 1 });
}
