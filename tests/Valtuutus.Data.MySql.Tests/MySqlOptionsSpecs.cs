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
            var options = new ValtuutusMySqlOptions(fixture.ServerType);
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

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public void A_blank_table_name_is_refused(string blank)
        {
            var options = new ValtuutusMySqlOptions(fixture.ServerType);

            ((Action)(() => _ = options with { TransactionsTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("TransactionsTableName");
            ((Action)(() => _ = options with { RelationsTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("RelationsTableName");
            ((Action)(() => _ = options with { AttributesTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("AttributesTableName");
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    public sealed class MySqlOptionsSpecs(MySqlFixture fixture) : MySqlOptionsSpecsBase(fixture), IClassFixture<MySqlFixture>;
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    public sealed class MySqlOptionsSpecs(MariaDbFixture fixture) : MySqlOptionsSpecsBase(fixture), IClassFixture<MariaDbFixture>;
}
