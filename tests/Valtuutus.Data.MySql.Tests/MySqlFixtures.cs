using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Respawn;
using Valtuutus.Data.Db;
using Valtuutus.Data.MySql.Weasel;
using Valtuutus.Data.Weasel;
using Valtuutus.Tests.Containers;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests;

/// <summary>
/// A database of its own on the shared MySQL or MariaDB server, migrated and reset before each test.
/// Each test class gets one, so classes run in parallel without seeing each other's rows.
/// </summary>
public abstract class MySqlFixtureBase : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    private readonly List<string> _databases = [];
    private string _serverConnectionString = default!;
    private MySqlConnection _dbConnection = default!;
    private Respawner _respawner = default!;

    public DbConnectionFactory DbFactory { get; private set; } = default!;

    public string ConnectionString { get; private set; } = default!;

    /// <summary>The name of this fixture's database.</summary>
    public string DatabaseName { get; } = SharedContainers.NewDatabaseName();

    public abstract MySqlServerType ServerType { get; }

    public ValtuutusMySqlOptions Options => new(ServerType);

    protected abstract Task<string> ServerAsync();

    public async Task InitializeAsync()
    {
        _serverConnectionString = await ServerAsync();
        var connectionString = await CreateDatabaseAsync(DatabaseName);
        ConnectionString = connectionString;
        DbFactory = () => new MySqlConnection(connectionString);
        _dbConnection = (MySqlConnection)DbFactory();
        await _dbConnection.OpenAsync();
        _respawner = await Respawner.CreateAsync(_dbConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.MySql,
            SchemasToInclude = [DatabaseName],
            TablesToInclude = ["transactions", "relation_tuples", "attributes"],
            WithReseed = true,
        });
    }

    /// <summary>
    /// Creates another database on the same server, empty and migrated, and returns a connection
    /// string whose default database it is. The fixture drops it with its own.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS `{name}`; CREATE DATABASE `{name}`");
        _databases.Add(name);

        var connectionString = new MySqlConnectionStringBuilder(_serverConnectionString) { Database = name }.ConnectionString;
        var services = new ServiceCollection();
        services.AddMySql(_ => () => new MySqlConnection(connectionString), Options).AddMySqlWeasel();
        await using var sp = services.BuildServiceProvider();
        await sp.ApplyValtuutusSchemaAsync();
        return connectionString;
    }

    public async Task ResetDatabaseAsync()
    {
        await _respawner.ResetAsync(_dbConnection);
    }

    public async Task DisposeAsync()
    {
        await _dbConnection.DisposeAsync();
        foreach (var name in _databases)
        {
            await using (var pooled = new MySqlConnection(new MySqlConnectionStringBuilder(_serverConnectionString) { Database = name }.ConnectionString))
                await MySqlConnection.ClearPoolAsync(pooled);
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS `{name}`");
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new MySqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class MySqlFixture : MySqlFixtureBase
{
    public override MySqlServerType ServerType => MySqlServerType.MySql;

    protected override Task<string> ServerAsync() => SharedContainers.MySqlAsync();
}

public sealed class MariaDbFixture : MySqlFixtureBase
{
    public override MySqlServerType ServerType => MySqlServerType.MariaDb;

    protected override Task<string> ServerAsync() => SharedContainers.MariaDbAsync();
}
