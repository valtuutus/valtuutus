using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Containers;

namespace Valtuutus.Data.SqlServer.Weasel.Tests;

public sealed class SqlServerWeaselFixture : IAsyncLifetime
{
    private readonly List<string> _databases = [];
    private string _server = default!;

    public async Task InitializeAsync() => _server = await SharedContainers.SqlServerAsync();

    public async Task DisposeAsync()
    {
        foreach (var name in _databases)
        {
            await using (var pooled = new SqlConnection(ConnectionStringOf(name)))
                SqlConnection.ClearPool(pooled);
            await SharedSqlServer.DropDatabaseAsync(_server, name);
        }
    }

    /// <summary>Creates an empty database on the shared server and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string? collation = null)
    {
        var name = SharedContainers.NewDatabaseName();
        await ExecuteOnServerAsync(collation is null ? $"CREATE DATABASE [{name}]" : $"CREATE DATABASE [{name}] COLLATE {collation}");
        _databases.Add(name);
        return ConnectionStringOf(name);
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var admin = new SqlConnection(_server);
        await admin.OpenAsync();
        await ScriptInstaller.Exec(admin, sql);
    }

    private string ConnectionStringOf(string database) =>
        new SqlConnectionStringBuilder(_server) { InitialCatalog = database }.ConnectionString;

    public static ServiceProvider BuildServices(string connectionString, ValtuutusSqlServerOptions dbOptions,
        Action<ValtuutusSqlServerWeaselOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSqlServer(_ => () => new SqlConnection(connectionString), dbOptions).AddSqlServerWeasel(configure);
        return services.BuildServiceProvider();
    }

    public static ValtuutusSqlServerOptions CustomNames => new()
    {
        Schema = "authz", TransactionsTableName = "my_tx", RelationsTableName = "my_tuples", AttributesTableName = "my_attrs"
    };

}
