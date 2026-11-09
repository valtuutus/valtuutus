using System.Security.Cryptography;
using System.Text;
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
/// A changed server configuration starts a new container under a new name; the old one stays until
/// removed.
/// </remarks>
public static class SharedContainers
{
    /// <summary>The label every shared server carries, for finding them outside a test run.</summary>
    public const string Label = "valtuutus-tests";

    public const string PostgresPassword = "Valtuutus123";
    public const string MySqlPassword = "Valtuutus123";
    public const string SqlServerPassword = "Valtuutus123!";

    private const string PostgresImage = "postgres:15.1";
    private static readonly string[] PostgresCommand = ["-c", "max_connections=500"];

    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04";

    private const string MySqlImage = "mysql:8.4";
    private const string MariaDbImage = "mariadb:10.11";
    private static readonly string[] MySqlCommand =
    [
        "--max-connections=1000", "--skip-log-bin", "--innodb-flush-log-at-trx-commit=0",
        "--innodb-doublewrite=0", "--sync-binlog=0", "--innodb-use-native-aio=0",
    ];

    private static readonly Lazy<Task<string>> PostgresServer = new(() => StartAsync("postgres",
        new PostgreSqlBuilder(PostgresImage)
            .WithUsername("Valtuutus")
            .WithPassword(PostgresPassword)
            .WithDatabase("Valtuutus")
            .WithCommand(PostgresCommand)
            .WithTmpfsMount("/var/lib/postgresql/data")
            .WithName(ContainerName("postgres", [PostgresImage, PostgresPassword, ..PostgresCommand]))
            .WithLabel(Label, "true")
            .WithReuse(true)
            .Build()));

    private static readonly Lazy<Task<string>> SqlServerServer = new(() => StartAsync("sqlserver",
        new MsSqlBuilder(SqlServerImage)
            .WithPassword(SqlServerPassword)
            .WithName(ContainerName("sqlserver", [SqlServerImage, SqlServerPassword]))
            .WithLabel(Label, "true")
            .WithReuse(true)
            .Build()));

    private static readonly Lazy<Task<string>> MySqlServer = new(() => StartAsync("mysql",
        new MySqlBuilder(MySqlImage)
            .WithUsername("root")
            .WithPassword(MySqlPassword)
            .WithDatabase("valtuutus")
            .WithCommand(MySqlCommand)
            .WithTmpfsMount("/var/lib/mysql")
            .WithName(ContainerName("mysql", [MySqlImage, MySqlPassword, ..MySqlCommand]))
            .WithLabel(Label, "true")
            .WithReuse(true)
            .Build()));

    private static readonly Lazy<Task<string>> MariaDbServer = new(() => StartAsync("mariadb",
        new MariaDbBuilder(MariaDbImage)
            .WithUsername("root")
            .WithPassword(MySqlPassword)
            .WithDatabase("valtuutus")
            .WithCommand(MySqlCommand)
            .WithTmpfsMount("/var/lib/mysql")
            .WithName(ContainerName("mariadb", [MariaDbImage, MySqlPassword, ..MySqlCommand]))
            .WithLabel(Label, "true")
            .WithReuse(true)
            .Build()));

    // Reuse finds a container by a hash of its configuration, so a fixed name would collide with the
    // container of an earlier configuration. The name carries a hash of what the server is started
    // with: a changed image or flag starts a new container under a new name.
    private static string ContainerName(string server, string[] configuration)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', configuration)));
        return $"{Label}-{server}-{Convert.ToHexStringLower(hash.AsSpan(0, 3))}";
    }

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
