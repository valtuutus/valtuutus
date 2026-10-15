using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

// The shared LookupEntity specs with three hops per query, so the deeper level statements stay
// equal to the V1 engine on joined grants, shortcuts and cycles.
[Collection("SqlServerSpec")]
public sealed class LookupEntityEngineThreeLevelSpecs : BaseLookupEntityEngineSpecs
{
    public LookupEntityEngineThreeLevelSpecs(SqlServerFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            new ValtuutusSqlServerOptions("dbo", "transactions", "relation_tuples", "attributes") { LookupHopsPerQuery = 3 });
}
