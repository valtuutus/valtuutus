using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests
{
    public abstract class MySqlOptionsSpecsBase(MySqlFixtureBase fixture)
    {
        [Fact]
        public void AddMySql_Should_Register_Options_And_Resolve_Reader()
        {
            var options = new ValtuutusMySqlOptions(fixture.ServerType, "", "transactions", "relation_tuples", "attributes");
            var services = new ServiceCollection().AddValtuutusCore(TestsConsts.DefaultSchema);
            services.AddMySql(_ => fixture.DbFactory, options);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            scope.ServiceProvider.GetRequiredService<ValtuutusMySqlOptions>().Should().BeSameAs(options);
            scope.ServiceProvider.GetRequiredService<IDataReaderProvider>().Should().NotBeNull();
        }

        [Fact]
        public void Options_Should_Default_To_Unqualified_Tables()
        {
            var options = new ValtuutusMySqlOptions(fixture.ServerType);
            options.Schema.Should().BeEmpty();
            options.TransactionsTableName.Should().Be("transactions");
            options.RelationsTableName.Should().Be("relation_tuples");
            options.AttributesTableName.Should().Be("attributes");
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class MySqlOptionsSpecs(MySqlFixture fixture) : MySqlOptionsSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class MySqlOptionsSpecs(MariaDbFixture fixture) : MySqlOptionsSpecsBase(fixture);
}
