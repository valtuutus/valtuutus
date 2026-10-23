using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests.MySql
{
    // The shared LookupSubject specs with one userset level per query, next to LookupSubjectEngineSpecs
    // on the provider default, so both shapes of the plan give the same answers.
    [Collection("MySqlSpec")]
    public sealed class LookupSubjectEngineOneLevelSpecs(MySqlFixture fixture) : BaseLookupSubjectEngineSpecs(fixture)
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options with { LookupHopsPerQuery = 1 });
    }
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    // The shared LookupSubject specs with one userset level per query, next to LookupSubjectEngineSpecs
    // on the provider default, so both shapes of the plan give the same answers.
    [Collection("MariaDbSpec")]
    public sealed class LookupSubjectEngineOneLevelSpecs(MariaDbFixture fixture) : BaseLookupSubjectEngineSpecs(fixture)
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options with { LookupHopsPerQuery = 1 });
    }
}
