using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Respawn;
using Testcontainers.MsSql;
using Valtuutus.Data.Db;
using Valtuutus.Data.SqlServer.Weasel;
using Valtuutus.Data.Weasel;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;


[CollectionDefinition("SqlServerAuthzSpec")]
public sealed class SqlServerAuthzSpecsFixture : ICollectionFixture<NonDefaultSchemaSqlServerFixture>
{
}

/// <summary>
/// Provisions the Valtuutus tables under a non-default SQL schema ("authz") so every query's
/// schema qualification is exercised end-to-end.
///
/// The <see cref="DbQueryCacheKey"/>-keyed ConcurrentDictionary cache lets this fixture share a process with the default-schema (dbo) tests.
/// </summary>
public class NonDefaultSchemaSqlServerFixture : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    public const string Schema = "authz";

    public DbConnectionFactory DbFactory { get; private set; } = default!;
    private Respawner _respawner = default!;

    private readonly MsSqlContainer _dbContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
        .WithPassword("Valtuutus123!")
        .WithName($"mssql-nondefaultschema-integration-tests-{Guid.NewGuid()}")
        .Build();

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        await CreateDatabase("Valtuutus");
        DbFactory = () => new SqlConnection(_dbContainer.GetConnectionString());
        var services = new ServiceCollection();
        services.AddSqlServer(_ => DbFactory,
                new ValtuutusSqlServerOptions(Schema, "transactions", "relation_tuples", "attributes"))
            .AddSqlServerWeasel();
        await using (var sp = services.BuildServiceProvider())
            await sp.ApplyValtuutusSchemaAsync();
        await SetupRespawnerAsync();
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new SqlConnection(_dbContainer.GetConnectionString());
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    private async Task SetupRespawnerAsync()
    {
        await using var connection = new SqlConnection(_dbContainer.GetConnectionString());
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            SchemasToInclude = new[] { Schema },
        });
    }

    public async Task DisposeAsync()
    {
        await _dbContainer.DisposeAsync();
    }

    private async Task CreateDatabase(string databaseName)
    {
        var createDatabaseScript = $@"
        IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = '{databaseName}')
          BEGIN
            CREATE DATABASE [{databaseName}];
          END;
      ";

        await _dbContainer.ExecScriptAsync(createDatabaseScript)
            .ConfigureAwait(false);
    }

}
