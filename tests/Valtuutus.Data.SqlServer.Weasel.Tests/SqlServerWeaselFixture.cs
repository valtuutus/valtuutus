using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace Valtuutus.Data.SqlServer.Weasel.Tests;

[CollectionDefinition("SqlServerWeasel")]
public sealed class SqlServerWeaselCollection : ICollectionFixture<SqlServerWeaselFixture>;

public sealed class SqlServerWeaselFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
        .WithName($"mssql-weasel-tests-{Guid.NewGuid()}")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync(string? collation = null)
    {
        var name = $"db_{Guid.NewGuid():N}";
        await using var admin = new SqlConnection(_container.GetConnectionString());
        await admin.OpenAsync();
        await ScriptInstaller.Exec(admin, collation is null ? $"CREATE DATABASE [{name}]" : $"CREATE DATABASE [{name}] COLLATE {collation}");
        return new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = name }.ConnectionString;
    }

    public static ServiceProvider BuildServices(string connectionString, ValtuutusSqlServerOptions dbOptions,
        Action<ValtuutusSqlServerWeaselOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSqlServer(_ => () => new SqlConnection(connectionString), dbOptions).AddSqlServerWeasel(configure);
        return services.BuildServiceProvider();
    }

    public static ValtuutusSqlServerOptions CustomNames => new("authz", "my_tx", "my_tuples", "my_attrs");

}
