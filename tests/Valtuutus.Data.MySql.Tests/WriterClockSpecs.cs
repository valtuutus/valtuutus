using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests.MySql
{
    public sealed class WriterClockSpecs(MySqlFixture fixture) : BaseDbWriterClockSpecs(fixture), IClassFixture<MySqlFixture>
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options);
    }
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    public sealed class WriterClockSpecs(MariaDbFixture fixture) : BaseDbWriterClockSpecs(fixture), IClassFixture<MariaDbFixture>
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options);
    }
}
