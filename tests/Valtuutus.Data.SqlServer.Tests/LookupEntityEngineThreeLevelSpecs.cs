using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

// The shared LookupEntity specs with three hops per query, so the deeper level statements stay
// equal to the V1 engine on joined grants, shortcuts and cycles.
public sealed class LookupEntityEngineThreeLevelSpecs : BaseLookupEntityEngineSpecs, IClassFixture<SqlServerFixture>
{
    public LookupEntityEngineThreeLevelSpecs(SqlServerFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddSqlServer(_ => ((IWithDbConnectionFactory)Fixture).DbFactory,
            ((SqlServerFixture)Fixture).Options with { LookupHopsPerQuery = 3 });
}
