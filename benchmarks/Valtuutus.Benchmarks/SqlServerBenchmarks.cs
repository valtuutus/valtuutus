using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using Testcontainers.MsSql;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data.SqlServer;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SqlServerBenchmarks : BenchmarkBase
{
    // BenchmarkDotNet calls [GlobalSetup]/[GlobalCleanup] once per benchmark CASE (i.e. once
    // per [Benchmark] method), not once per class, regardless of toolchain. With 14 methods
    // here, a per-instance container would boot/migrate/seed 14 times. Lazy<Task<T>> makes the
    // container+migrate+seed run exactly once across all instances; every later Setup() call
    // just awaits the already-completed task.
    private static readonly Lazy<Task<(ServiceProvider ServiceProvider, ICheckEngine CheckEngine, ILookupEntityEngine LookupEntityEngine, ILookupSubjectEngine LookupSubjectEngine)>> Shared = new(InitializeAsync);

    private static async Task<(ServiceProvider, ICheckEngine, ILookupEntityEngine, ILookupSubjectEngine)> InitializeAsync()
    {
        var container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
            .WithPassword("Valtuutus123!")
            .WithName($"mssql-benchmarks-{Guid.NewGuid()}")
            .Build();
        await container.StartAsync();
        IDbConnection DbFactory() => new SqlConnection(container.GetConnectionString());
        var mssqlAssembly = typeof(ValtuutusSqlServerOptions).Assembly;
        var (serviceProvider, checkEngine, lookupEntityEngine, lookupSubjectEngine) = await CommonSetup.MigrateAndSeed(
            sc => sc.AddSqlServer(_ => DbFactory),
            Seeder.Seeder.GenerateData(),
            mssqlAssembly);

        // Mirrors the Postgres ANALYZE call: AUTO_CREATE_STATISTICS/AUTO_UPDATE_STATISTICS are
        // on by default and normally self-heal synchronously on first compile, but pin it
        // explicitly so benchmark numbers reflect steady-state plans rather than whatever the
        // very first query's auto-created (possibly low-sample) stats produced.
        await using (var connection = new SqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE STATISTICS relation_tuples WITH FULLSCAN; UPDATE STATISTICS attributes WITH FULLSCAN;";
            await command.ExecuteNonQueryAsync();
        }

        // GlobalCleanup fires once per benchmark case too, so real teardown can't live there
        // without tearing down the shared container after the first case. Tear down once, for
        // real, when the whole process exits instead.
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
