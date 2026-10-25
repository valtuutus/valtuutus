using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class CheckEngineSpecs(MySqlFixture fixture) : BaseCheckEngineSpecs(fixture)
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options);
    }
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class CheckEngineSpecs(MariaDbFixture fixture) : BaseCheckEngineSpecs(fixture)
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options);
    }
}
