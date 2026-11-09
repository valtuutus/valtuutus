using DotNet.Testcontainers.Containers;
using Testcontainers.MariaDb;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace Valtuutus.Tests.Containers;

/// <summary>
/// One database server per provider for the whole test run. Every test project and target framework
/// runs in its own process, so the servers are reusable containers with fixed names: the first process
/// to ask starts one, the others find it running and attach. Fixtures isolate themselves with a
/// database of their own on the shared server.
/// </summary>
/// <remarks>
/// Reused containers outlive the run, so the next run skips the server start. Remove them with
/// <c>podman rm -f $(podman ps -aq --filter label=valtuutus-tests)</c> (or the docker equivalent).
/// A changed server configuration starts a new container; the old one stays until removed.
/// </remarks>
public static class SharedContainers
{
    /// <summary>The label every shared server carries, for finding them outside a test run.</summary>
    public const string Label = "valtuutus-tests";

    public const string PostgresPassword = "Valtuutus123";
    public const string MySqlPassword = "Valtuutus123";
    public const string SqlServerPassword = "Valtuutus123!";

    private static readonly Lazy<Task<string>> PostgresServer = new(() => StartAsync("postgres",
        new PostgreSqlBuilder("postgres:15.1")
            .WithUsername("Valtuutus")
            .WithPassword(PostgresPassword)
            .WithDatabase("Valtuutus")
            .WithCommand("-c", "max_connections=500")
            .WithTmpfsMount("/var/lib/postgresql/data")
            .WithLabel(Label, "true")
            .WithReuse(true)
            .Build()));

    private static readonly Lazy<Task<string>> SqlServerServer = new(() => StartAsync("sqlserver",
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
            .WithPassword(SqlServerPassword)
            .WithLabel(Label, "true")
            .WithReuse(true)
            .Build()));

    private static readonly Lazy<Task<string>> MySqlServer = new(() => StartAsync("mysql",
        new MySqlBuilder("mysql:8.4")
            .WithUsername("root")
            .WithPassword(MySqlPassword)
            .WithDatabase("valtuutus")
            .WithCommand("--max-connections=1000", "--skip-log-bin", "--innodb-flush-log-at-trx-commit=0",
                "--innodb-doublewrite=0", "--sync-binlog=0", "--innodb-use-native-aio=0")
            .WithTmpfsMount("/var/lib/mysql")
            .WithLabel(Label, "true")
            .WithReuse(true)
            .Build()));

    private static readonly Lazy<Task<string>> MariaDbServer = new(() => StartAsync("mariadb",
        new MariaDbBuilder("mariadb:10.11")
            .WithUsername("root")
            .WithPassword(MySqlPassword)
            .WithDatabase("valtuutus")
            .WithCommand("--max-connections=1000", "--skip-log-bin", "--innodb-flush-log-at-trx-commit=0",
                "--innodb-doublewrite=0", "--sync-binlog=0", "--innodb-use-native-aio=0")
            .WithTmpfsMount("/var/lib/mysql")
            .WithLabel(Label, "true")
            .WithReuse(true)
            .Build()));

    /// <summary>A connection string to the shared Postgres server, as a superuser.</summary>
    public static Task<string> PostgresAsync() => PostgresServer.Value;

    /// <summary>A connection string to the shared SQL Server, as <c>sa</c>.</summary>
    public static Task<string> SqlServerAsync() => SqlServerServer.Value;

    /// <summary>A connection string to the shared MySQL server, as <c>root</c>.</summary>
    public static Task<string> MySqlAsync() => MySqlServer.Value;

    /// <summary>A connection string to the shared MariaDB server, as <c>root</c>.</summary>
    public static Task<string> MariaDbAsync() => MariaDbServer.Value;

    /// <summary>A database name no other fixture, process or earlier run uses.</summary>
    public static string NewDatabaseName() => $"t_{Guid.NewGuid():N}";

    private static async Task<string> StartAsync(string server, IDatabaseContainer container)
    {
        await using (await CrossProcessLock.AcquireAsync($"valtuutus-tests-{server}"))
            await container.StartAsync();
        return container.GetConnectionString();
    }
}
