using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests.MySql
{
    public sealed class ReadYourWritesSpecs(MySqlFixture fixture)
        : BaseDbReadYourWritesSpecs(fixture), IClassFixture<MySqlFixture>
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        // The sweeper removes rows older than a second, once a second.
        protected override TimeSpan EndedWriteRelease => TimeSpan.FromSeconds(2.5);

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options);
    }
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    public sealed class ReadYourWritesSpecs(MariaDbFixture fixture)
        : BaseDbReadYourWritesSpecs(fixture), IClassFixture<MariaDbFixture>
    {
        private MySqlFixtureBase Db => (MySqlFixtureBase)Fixture;

        // The sweeper removes rows older than a second, once a second.
        protected override TimeSpan EndedWriteRelease => TimeSpan.FromSeconds(2.5);

        protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
            services.AddMySql(_ => Db.DbFactory, Db.Options);
    }
}
