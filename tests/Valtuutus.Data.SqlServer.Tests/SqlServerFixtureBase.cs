using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Respawn;
using Testcontainers.MsSql;
using Valtuutus.Data.Db;
using Valtuutus.Data.SqlServer.Weasel;
using Valtuutus.Data.Weasel;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;


public abstract class SqlServerFixtureBase : IAsyncLifetime, IDatabaseFixture, IWithDbConnectionFactory
{
    public abstract ValtuutusSqlServerOptions Options { get; }

    public DbConnectionFactory DbFactory { get; private set; } = default!;
    private Respawner _respawner = default!;

    
    private readonly MsSqlContainer _dbContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
	    .WithPassword("Valtuutus123!")
	    .WithName($"mssql-integration-tests-{Guid.NewGuid()}")
	    .Build();
    
    public async Task InitializeAsync()
    {
	    await _dbContainer.StartAsync();
	    await CreateDatabase("Valtuutus");
        DbFactory = () => new SqlConnection(_dbContainer.GetConnectionString());
        var services = new ServiceCollection();
        services.AddSqlServer(_ => DbFactory,
                Options)
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
		    SchemasToInclude = [Options.Schema],
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

[CollectionDefinition("SqlServerSpec")]
public sealed class SqlServerSpecsFixture : ICollectionFixture<SqlServerFixture>
{
}

public class SqlServerFixture : SqlServerFixtureBase
{
    public override ValtuutusSqlServerOptions Options { get; } = new();
}
