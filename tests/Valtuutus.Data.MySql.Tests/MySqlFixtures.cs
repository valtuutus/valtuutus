using DotNet.Testcontainers.Containers;
using MySqlConnector;
using Respawn;
using Testcontainers.MariaDb;
using Testcontainers.MySql;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests;

[CollectionDefinition("MySqlSpec")]
public sealed class MySqlSpecsFixture : ICollectionFixture<MySqlFixture>;

[CollectionDefinition("MariaDbSpec")]
public sealed class MariaDbSpecsFixture : ICollectionFixture<MariaDbFixture>;

public abstract class MySqlFixtureBase : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    protected const string Database = "valtuutus";
    protected const string Username = "root";
    protected const string Password = "Valtuutus123";

    private IDatabaseContainer? _container;
    private MySqlConnection _dbConnection = default!;
    private Respawner _respawner = default!;

    public DbConnectionFactory DbFactory { get; private set; } = default!;

    public string ConnectionString { get; private set; } = default!;

    public abstract MySqlServerType ServerType { get; }

    public ValtuutusMySqlOptions Options => new(ServerType);

    protected abstract IDatabaseContainer CreateContainer();

    public async Task InitializeAsync()
    {
        _container = CreateContainer();
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString();
        ConnectionString = connectionString;
        DbFactory = () => new MySqlConnection(connectionString);
        _dbConnection = (MySqlConnection)DbFactory();
        await _dbConnection.OpenAsync();
        await RunMigrationsAsync(_dbConnection);
        _respawner = await Respawner.CreateAsync(_dbConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.MySql,
            TablesToInclude = ["transactions", "relation_tuples", "attributes"],
            WithReseed = true,
        });
    }

    /// <summary>
    /// Creates another database on the same server, empty and migrated, and returns a connection
    /// string whose default database it is.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using (var command = _dbConnection.CreateCommand())
        {
            command.CommandText = $"DROP DATABASE IF EXISTS `{name}`; CREATE DATABASE `{name}`";
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new MySqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await RunMigrationsAsync(connection);
        return connectionString;
    }

    private static async Task RunMigrationsAsync(MySqlConnection connection)
    {
        var migrationAssembly = typeof(MySqlServerType).Assembly;
        var migrations = migrationAssembly
            .GetManifestResourceNames()
            .Where(x => x.EndsWith(".sql"))
            .OrderBy(x => x);

        foreach (var migrationFile in migrations)
        {
            await using var stream = migrationAssembly.GetManifestResourceStream(migrationFile);
            using var reader = new StreamReader(stream!);
            var migrationSql = await reader.ReadToEndAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = migrationSql;
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task ResetDatabaseAsync()
    {
        await _respawner.ResetAsync(_dbConnection);
    }

    public async Task DisposeAsync()
    {
        await _dbConnection.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

public sealed class MySqlFixture : MySqlFixtureBase
{
    public override MySqlServerType ServerType => MySqlServerType.MySql;

    protected override IDatabaseContainer CreateContainer() => new MySqlBuilder("mysql:8.4")
        .WithUsername(Username)
        .WithPassword(Password)
        .WithDatabase(Database)
        .WithName($"mysql-integration-tests-{Guid.NewGuid()}")
        .Build();
}

public sealed class MariaDbFixture : MySqlFixtureBase
{
    public override MySqlServerType ServerType => MySqlServerType.MariaDb;

    protected override IDatabaseContainer CreateContainer() => new MariaDbBuilder("mariadb:10.11")
        .WithUsername(Username)
        .WithPassword(Password)
        .WithDatabase(Database)
        .WithName($"mariadb-integration-tests-{Guid.NewGuid()}")
        .Build();
}
