using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests.MySql
{
    // The shared LookupEntity specs with three hops per query, so the deeper level statements stay
    // correct on joined grants, shortcuts and cycles.
    [Collection("MySqlSpec")]
    public sealed class LookupEntityEngineThreeLevelSpecs(MySqlFixture fixture) : BaseLookupEntityEngineSpecs(fixture)
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options with { LookupHopsPerQuery = 3 });
    }
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    // The shared LookupEntity specs with three hops per query, so the deeper level statements stay
    // correct on joined grants, shortcuts and cycles.
    [Collection("MariaDbSpec")]
    public sealed class LookupEntityEngineThreeLevelSpecs(MariaDbFixture fixture) : BaseLookupEntityEngineSpecs(fixture)
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options with { LookupHopsPerQuery = 3 });
    }
}
