using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres.Weasel.Tests;

[CollectionDefinition("PostgresWeasel")]
public sealed class PostgresWeaselCollection : ICollectionFixture<PostgresWeaselFixture>;

public sealed class PostgresWeaselFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:15.1")
        .WithUsername("Valtuutus").WithPassword("Valtuutus123").WithDatabase("Valtuutus")
        .WithName($"pg-weasel-tests-{Guid.NewGuid()}")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"db_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(_container.GetConnectionString());
        await admin.OpenAsync();
        await ScriptInstaller.Exec(admin, $"CREATE DATABASE {name}");
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public static ServiceProvider BuildServices(string connectionString, ValtuutusPostgresOptions dbOptions,
        Action<ValtuutusPostgresWeaselOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddPostgres(_ => () => new NpgsqlConnection(connectionString), dbOptions).AddPostgresWeasel(configure);
        return services.BuildServiceProvider();
    }

    public static ValtuutusPostgresOptions CustomNames => new("authz", "my_tx", "my_tuples", "my_attrs");

    public static ValtuutusPostgresOptions DefaultNames => new("public", "transactions", "relation_tuples", "attributes");
}
