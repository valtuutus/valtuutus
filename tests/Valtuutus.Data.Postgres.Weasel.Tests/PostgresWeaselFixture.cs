using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Containers;

namespace Valtuutus.Data.Postgres.Weasel.Tests;

public sealed class PostgresWeaselFixture : IAsyncLifetime
{
    private readonly List<string> _databases = [];
    private string _server = default!;

    public async Task InitializeAsync() => _server = await SharedContainers.PostgresAsync();

    public async Task DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await using (var pooled = new NpgsqlConnection(ConnectionStringOf(name)))
                NpgsqlConnection.ClearPool(pooled);
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
    }

    /// <summary>Creates an empty database on the shared server and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = SharedContainers.NewDatabaseName();
        await ExecuteOnServerAsync($"CREATE DATABASE {name}");
        _databases.Add(name);
        return ConnectionStringOf(name);
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(_server);
        await admin.OpenAsync();
        await ScriptInstaller.Exec(admin, sql);
    }

    private string ConnectionStringOf(string database) =>
        new NpgsqlConnectionStringBuilder(_server) { Database = database }.ConnectionString;

    public static ServiceProvider BuildServices(string connectionString, ValtuutusPostgresOptions dbOptions,
        Action<ValtuutusPostgresWeaselOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddPostgres(_ => () => new NpgsqlConnection(connectionString), dbOptions).AddPostgresWeasel(configure);
        return services.BuildServiceProvider();
    }

    public static ValtuutusPostgresOptions CustomNames => new()
    {
        Schema = "authz", TransactionsTableName = "my_tx", RelationsTableName = "my_tuples", AttributesTableName = "my_attrs"
    };

}
