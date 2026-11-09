using AwesomeAssertions;

namespace Valtuutus.Data.SqlServer.Tests;

public sealed class SqlServerOptionsSpecs
{
    [Fact]
    public void Names_default_to_the_dbo_schema_and_the_standard_tables()
    {
        var options = new ValtuutusSqlServerOptions();

        options.Schema.Should().Be("dbo");
        options.TransactionsTableName.Should().Be("transactions");
        options.RelationsTableName.Should().Be("relation_tuples");
        options.AttributesTableName.Should().Be("attributes");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_schema_or_table_name_is_refused(string blank)
    {
        var options = new ValtuutusSqlServerOptions();

        ((Action)(() => _ = options with { Schema = blank })).Should().Throw<ArgumentNullException>().WithParameterName("Schema");
        ((Action)(() => _ = options with { TransactionsTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("TransactionsTableName");
        ((Action)(() => _ = options with { RelationsTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("RelationsTableName");
        ((Action)(() => _ = options with { AttributesTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("AttributesTableName");
    }
}
