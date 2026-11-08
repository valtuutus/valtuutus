using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Valtuutus.Data.Db;
using Valtuutus.Data.Postgres.Weasel;
using Valtuutus.Data.Weasel;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;


[CollectionDefinition("PostgreSqlSpec")]
public sealed class PostgresSpecsFixture : ICollectionFixture<PostgresFixture>
{
}


public class PostgresFixture : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    public ValtuutusPostgresOptions Options { get; } = new();

    public DbConnectionFactory DbFactory { get; private set; } = default!;
    private NpgsqlConnection _dbConnection = default!;
    private Respawner _respawner = default!;

    
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:15.1")
        .WithUsername("Valtuutus")
        .WithPassword("Valtuutus123")
        .WithDatabase("Valtuutus")
        .WithName($"pg-integration-tests-{Guid.NewGuid()}")
        .Build();
    
    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        DbFactory = () => new NpgsqlConnection(_dbContainer.GetConnectionString());
        _dbConnection = (NpgsqlConnection)DbFactory();
        await _dbConnection.OpenAsync();
        await using (var sp = BuildServices())
            await sp.ApplyValtuutusSchemaAsync();
        await SetupRespawnerAsync();

    }
    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddPostgres(_ => DbFactory,
                Options)
            .AddPostgresWeasel();
        return services.BuildServiceProvider();
    }

    public async Task ResetDatabaseAsync()
    {
        await _respawner.ResetAsync(_dbConnection);
    }
    
    private async Task SetupRespawnerAsync()
    {
        _respawner = await Respawner.CreateAsync(_dbConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            WithReseed = true,
        });
    }
    

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
    }

}
