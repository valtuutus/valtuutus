using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class PostgresOptionsSpecs : IAsyncLifetime
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
        var options = new ValtuutusPostgresOptions("public", "transactions", "relation_tuples", "attributes")
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
}
