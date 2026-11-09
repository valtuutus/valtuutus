using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Respawn;
using Valtuutus.Data.Db;
using Valtuutus.Data.Postgres.Weasel;
using Valtuutus.Data.Weasel;
using Valtuutus.Tests.Containers;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

/// <summary>
/// A database of its own on the shared Postgres server, migrated and reset before each test. Each
/// test class gets one, so classes run in parallel without seeing each other's rows.
/// </summary>
public class PostgresFixture : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    public ValtuutusPostgresOptions Options { get; } = new();

    public DbConnectionFactory DbFactory { get; private set; } = default!;
    private string _serverConnectionString = default!;
    private string _connectionString = default!;
    private readonly string _database = SharedContainers.NewDatabaseName();
    private NpgsqlConnection _dbConnection = default!;
    private Respawner _respawner = default!;

    public async Task InitializeAsync()
    {
        _serverConnectionString = await SharedContainers.PostgresAsync();
        await ExecuteOnServerAsync($"CREATE DATABASE {_database}");
        _connectionString = new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = _database }.ConnectionString;
        DbFactory = () => new NpgsqlConnection(_connectionString);
        _dbConnection = (NpgsqlConnection)DbFactory();
        await _dbConnection.OpenAsync();
        await using (var sp = BuildServices())
            await sp.ApplyValtuutusSchemaAsync();
        _respawner = await Respawner.CreateAsync(_dbConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            WithReseed = true,
        });
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddPostgres(_ => DbFactory, Options).AddPostgresWeasel();
        return services.BuildServiceProvider();
    }

    public async Task ResetDatabaseAsync()
    {
        await _respawner.ResetAsync(_dbConnection);
    }

    public async Task DisposeAsync()
    {
        await _dbConnection.DisposeAsync();
        await using (var pooled = new NpgsqlConnection(_connectionString))
            NpgsqlConnection.ClearPool(pooled);
        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)");
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
