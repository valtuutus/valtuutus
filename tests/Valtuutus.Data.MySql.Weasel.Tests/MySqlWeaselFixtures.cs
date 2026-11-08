using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Testcontainers.MariaDb;
using Testcontainers.MySql;

namespace Valtuutus.Data.MySql.Weasel.Tests;

[CollectionDefinition("MySqlWeasel")]
public sealed class MySqlWeaselCollection : ICollectionFixture<MySqlWeaselFixture>;

[CollectionDefinition("MariaDbWeasel")]
public sealed class MariaDbWeaselCollection : ICollectionFixture<MariaDbWeaselFixture>;

public abstract class MySqlWeaselFixtureBase : IAsyncLifetime
{
    private IDatabaseContainer _container = default!;

    public abstract MySqlServerType ServerType { get; }

    protected abstract IDatabaseContainer CreateContainer();

    public async Task InitializeAsync()
    {
        _container = CreateContainer();
        await _container.StartAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database and returns its name and a connection string defaulting to it.</summary>
    public async Task<(string Name, string ConnectionString)> CreateDatabaseAsync()
    {
        var name = $"db_{Guid.NewGuid():N}";
        await using var admin = new MySqlConnection(_container.GetConnectionString());
        await admin.OpenAsync();
        await ScriptInstaller.Exec(admin, $"CREATE DATABASE `{name}`");
        return (name, new MySqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString);
    }

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

    protected override IDatabaseContainer CreateContainer() => new MySqlBuilder("mysql:8.4")
        .WithUsername("root").WithPassword("Valtuutus123").WithDatabase("valtuutus")
        .WithName($"mysql-weasel-tests-{Guid.NewGuid()}")
        .Build();
}

public sealed class MariaDbWeaselFixture : MySqlWeaselFixtureBase
{
    public override MySqlServerType ServerType => MySqlServerType.MariaDb;

    protected override IDatabaseContainer CreateContainer() => new MariaDbBuilder("mariadb:10.11")
        .WithUsername("root").WithPassword("Valtuutus123").WithDatabase("valtuutus")
        .WithName($"mariadb-weasel-tests-{Guid.NewGuid()}")
        .Build();
}
