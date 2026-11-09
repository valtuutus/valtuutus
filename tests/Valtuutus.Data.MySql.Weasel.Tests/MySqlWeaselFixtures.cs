using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Valtuutus.Tests.Containers;

namespace Valtuutus.Data.MySql.Weasel.Tests;

public abstract class MySqlWeaselFixtureBase : IAsyncLifetime
{
    private readonly List<string> _databases = [];
    private string _server = default!;

    public abstract MySqlServerType ServerType { get; }

    protected abstract Task<string> ServerAsync();

    public async Task InitializeAsync() => _server = await ServerAsync();

    public async Task DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await using (var pooled = new MySqlConnection(ConnectionStringOf(name)))
                await MySqlConnection.ClearPoolAsync(pooled);
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS `{name}`");
        }
    }

    /// <summary>Creates an empty database on the shared server and returns its name and a connection string defaulting to it.</summary>
    public async Task<(string Name, string ConnectionString)> CreateDatabaseAsync()
    {
        var name = SharedContainers.NewDatabaseName();
        await ExecuteOnServerAsync($"CREATE DATABASE `{name}`");
        _databases.Add(name);
        return (name, ConnectionStringOf(name));
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var admin = new MySqlConnection(_server);
        await admin.OpenAsync();
        await ScriptInstaller.Exec(admin, sql);
    }

    private string ConnectionStringOf(string database) =>
        new MySqlConnectionStringBuilder(_server) { Database = database }.ConnectionString;

    public ServiceProvider BuildServices(string connectionString, ValtuutusMySqlOptions dbOptions,
        Action<ValtuutusMySqlWeaselOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddMySql(_ => () => new MySqlConnection(connectionString), dbOptions).AddMySqlWeasel(configure);
        return services.BuildServiceProvider();
    }

    /// <summary>Default options: tables in the connection's own database.</summary>
    public ValtuutusMySqlOptions DefaultNames => new(ServerType);

    /// <summary>Custom table names, in the named database.</summary>
    public ValtuutusMySqlOptions CustomNames(string database) => new(ServerType, database, "my_tx", "my_tuples", "my_attrs");
}

public sealed class MySqlWeaselFixture : MySqlWeaselFixtureBase
{
    public override MySqlServerType ServerType => MySqlServerType.MySql;

    protected override Task<string> ServerAsync() => SharedContainers.MySqlAsync();
}

public sealed class MariaDbWeaselFixture : MySqlWeaselFixtureBase
{
    public override MySqlServerType ServerType => MySqlServerType.MariaDb;

    protected override Task<string> ServerAsync() => SharedContainers.MariaDbAsync();
}
