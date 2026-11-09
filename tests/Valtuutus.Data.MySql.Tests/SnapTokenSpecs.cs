using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests.MySql
{
    public sealed class SnapTokenSpecs(MySqlFixture fixture) : BaseSnapTokenSpecs(fixture), IClassFixture<MySqlFixture>
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options);
    }
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    public sealed class SnapTokenSpecs(MariaDbFixture fixture) : BaseSnapTokenSpecs(fixture), IClassFixture<MariaDbFixture>
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options);
    }
}
