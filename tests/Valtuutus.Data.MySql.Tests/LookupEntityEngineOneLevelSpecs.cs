using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests.MySql
{
    // The shared LookupEntity specs with one hop per query, next to LookupEntityEngineSpecs on the
    // provider default, so both shapes of the walk give the same answers.
    [Collection("MySqlSpec")]
    public sealed class LookupEntityEngineOneLevelSpecs(MySqlFixture fixture) : BaseLookupEntityEngineSpecs(fixture)
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options with { LookupHopsPerQuery = 1 });
    }
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    // The shared LookupEntity specs with one hop per query, next to LookupEntityEngineSpecs on the
    // provider default, so both shapes of the walk give the same answers.
    [Collection("MariaDbSpec")]
    public sealed class LookupEntityEngineOneLevelSpecs(MariaDbFixture fixture) : BaseLookupEntityEngineSpecs(fixture)
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options with { LookupHopsPerQuery = 1 });
    }
}
