using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests.MySql
{
    // The shared LookupSubject specs with one userset level per query, next to LookupSubjectEngineSpecs
    // on the provider default, so both shapes of the plan give the same answers.
    public sealed class LookupSubjectEngineOneLevelSpecs(MySqlFixture fixture) : BaseLookupSubjectEngineSpecs(fixture), IClassFixture<MySqlFixture>
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
    public sealed class LookupSubjectEngineOneLevelSpecs(MariaDbFixture fixture) : BaseLookupSubjectEngineSpecs(fixture), IClassFixture<MariaDbFixture>
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options with { LookupHopsPerQuery = 1 });
    }
}
