using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Respawn;
using Valtuutus.Data.Db;
using Valtuutus.Data.SqlServer.Weasel;
using Valtuutus.Data.Weasel;
using Valtuutus.Tests.Containers;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

/// <summary>
/// A database of its own on the shared SQL Server, migrated and reset before each test. Each test
/// class gets one, so classes run in parallel without seeing each other's rows.
/// </summary>
public abstract class SqlServerFixtureBase : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    public abstract ValtuutusSqlServerOptions Options { get; }

    public DbConnectionFactory DbFactory { get; private set; } = default!;
    private readonly string _database = SharedContainers.NewDatabaseName();
    private string _serverConnectionString = default!;
    private string _connectionString = default!;
    private Respawner _respawner = default!;

    public async Task InitializeAsync()
    {
        _serverConnectionString = await SharedContainers.SqlServerAsync();
        await ExecuteOnServerAsync($"CREATE DATABASE [{_database}]");
        _connectionString = new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = _database }.ConnectionString;
        DbFactory = () => new SqlConnection(_connectionString);
        var services = new ServiceCollection();
        services.AddSqlServer(_ => DbFactory, Options).AddSqlServerWeasel();
        await using (var sp = services.BuildServiceProvider())
            await sp.ApplyValtuutusSchemaAsync();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            SchemasToInclude = [Options.Schema],
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    public async Task DisposeAsync()
    {
        await using (var pooled = new SqlConnection(_connectionString))
            SqlConnection.ClearPool(pooled);
        await SharedSqlServer.DropDatabaseAsync(_serverConnectionString, _database);
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new SqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

public class SqlServerFixture : SqlServerFixtureBase
{
    public override ValtuutusSqlServerOptions Options { get; } = new();
}
