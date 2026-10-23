using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using System.Data;
using Testcontainers.MySql;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data.MySql;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class MySqlBenchmarks : BenchmarkBase
{
    // See SqlServerBenchmarks: GlobalSetup/GlobalCleanup fire once per benchmark case (once per
    // method), not once per class, regardless of toolchain. Lazy<Task<T>> makes the
    // container+migrate+seed run exactly once across all instances.
    private static readonly Lazy<Task<(ServiceProvider ServiceProvider, ICheckEngine CheckEngine, ILookupEntityEngine LookupEntityEngine, ILookupSubjectEngine LookupSubjectEngine)>> Shared = new(InitializeAsync);

    private static async Task<(ServiceProvider, ICheckEngine, ILookupEntityEngine, ILookupSubjectEngine)> InitializeAsync()
    {
        var container = new MySqlBuilder("mysql:8.4")
            .WithUsername("root")
            .WithPassword("Valtuutus123")
            .WithDatabase("valtuutus")
            .WithName($"mysql-benchmarks-{Guid.NewGuid()}")
            .Build();
        await container.StartAsync();
        // Plaintext like the Pg and MariaDB containers, so no provider's numbers include TLS. Without
        // TLS, the root account's caching_sha2_password login needs the server's public key.
        var (serviceProvider, checkEngine, lookupEntityEngine, lookupSubjectEngine) =
            await MySqlBenchmarkSetup.MigrateSeedAndAnalyze(
                container.GetConnectionString() + ";SslMode=None;AllowPublicKeyRetrieval=True",
                MySqlServerType.MySql);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            container.DisposeAsync().AsTask().GetAwaiter().GetResult();
            serviceProvider.Dispose();
        };

        return (serviceProvider, checkEngine, lookupEntityEngine, lookupSubjectEngine);
    }

    [GlobalSetup]
    public async Task Setup()
    {
        (_, _checkEngine, _lookupEntityEngine, _lookupSubjectEngine) = await Shared.Value;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // No-op: shared container/provider are torn down once via ProcessExit above.
    }
}

/// <summary>
/// Migration, seed and statistics shared by the MySQL and MariaDB benchmarks, which differ only in
/// the server image they start.
/// </summary>
internal static class MySqlBenchmarkSetup
{
    public static async Task<(ServiceProvider, ICheckEngine, ILookupEntityEngine, ILookupSubjectEngine)> MigrateSeedAndAnalyze(
        string connectionString, MySqlServerType serverType)
    {
        IDbConnection DbFactory() => new MySqlConnection(connectionString);
        var options = new ValtuutusMySqlOptions(serverType);
        var result = await CommonSetup.MigrateAndSeed(
            sc => sc.AddMySql(_ => DbFactory,
                int.TryParse(Environment.GetEnvironmentVariable("VALTUUTUS_LOOKUP_HOPS"), out var hops)
                    ? options with { LookupHopsPerQuery = hops }
                    : options),
            Seeder.Seeder.GenerateData(),
            typeof(ValtuutusMySqlOptions).Assembly);

        // InnoDB samples index statistics in the background after enough of a table changes, so
        // right after the bulk seed the optimizer may still see the tables as empty and pick join
        // orders and indexes that steady state never would. Analyzing now gives it the statistics
        // it would have shortly after a bulk load in production, so the benchmarks measure
        // realistic plans.
        await using (var conn = new MySqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ANALYZE TABLE relation_tuples, attributes, transactions";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetString(2) is "error")
                    throw new InvalidOperationException($"ANALYZE TABLE {reader.GetString(0)}: {reader.GetString(3)}");
            }
        }

        return result;
    }
}
