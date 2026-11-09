using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

public sealed class PostgresOptionsSpecs : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresOptionsSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await _fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void AddPostgres_Should_Register_Custom_Options_And_Resolve_Reader()
    {
        var options = new ValtuutusPostgresOptions()
        {
            MaxAutoPrepare = 8,
            AutoPrepareMinUsages = 3
        };

        var services = new ServiceCollection()
            .AddValtuutusCore(TestsConsts.DefaultSchema);

        services.AddPostgres(_ => ((IWithDbConnectionFactory)_fixture).DbFactory, options);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolvedOptions = scope.ServiceProvider.GetRequiredService<ValtuutusPostgresOptions>();
        var reader = scope.ServiceProvider.GetRequiredService<IDataReaderProvider>();

        resolvedOptions.MaxAutoPrepare.Should().Be(8);
        resolvedOptions.AutoPrepareMinUsages.Should().Be(3);
        reader.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_schema_or_table_name_is_refused(string blank)
    {
        var options = new ValtuutusPostgresOptions();

        ((Action)(() => _ = options with { Schema = blank })).Should().Throw<ArgumentNullException>().WithParameterName("Schema");
        ((Action)(() => _ = options with { TransactionsTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("TransactionsTableName");
        ((Action)(() => _ = options with { RelationsTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("RelationsTableName");
        ((Action)(() => _ = options with { AttributesTableName = blank })).Should().Throw<ArgumentNullException>().WithParameterName("AttributesTableName");
    }
}
