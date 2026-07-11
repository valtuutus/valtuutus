using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data;
using Testcontainers.PostgreSql;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data.Postgres;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PostgresBenchmarks : BenchmarkBase
{
    // See SqlServerBenchmarks: GlobalSetup/GlobalCleanup fire once per benchmark case (once per
    // method), not once per class, regardless of toolchain. Lazy<Task<T>> makes the
    // container+migrate+seed run exactly once across all instances.
    private static readonly Lazy<Task<(ServiceProvider ServiceProvider, ICheckEngine CheckEngine, ILookupEntityEngine LookupEntityEngine, ILookupSubjectEngine LookupSubjectEngine)>> Shared = new(InitializeAsync);

    private static async Task<(ServiceProvider, ICheckEngine, ILookupEntityEngine, ILookupSubjectEngine)> InitializeAsync()
    {
        var container = new PostgreSqlBuilder()
            .WithUsername("Valtuutus")
            .WithPassword("Valtuutus123")
            .WithDatabase("Valtuutus")
            .WithName($"pg-benchmarks-{Guid.NewGuid()}")
            .Build();
        await container.StartAsync();
        IDbConnection DbFactory() => new NpgsqlConnection(container.GetConnectionString());
        var pgAssembly = typeof(ValtuutusPostgresOptions).Assembly;
        var (serviceProvider, checkEngine, lookupEntityEngine, lookupSubjectEngine) = await CommonSetup.MigrateAndSeed(
            sc => sc.AddPostgres(_ => DbFactory),
            Seeder.Seeder.GenerateData(),
            pgAssembly);

        // The bulk seed write leaves relation_tuples/attributes with zero planner statistics
        // (autovacuum's autoanalyze hasn't run yet). Without stats, Postgres defaults to ~1-row
        // cost estimates everywhere and picks nested-loop plans over the wrong index for the
        // GetRelationsJoined(ByEntityIds) two-hop-collapse queries — 1000x+ slower than the
        // steady-state plan it picks once stats exist. Mirrors what autovacuum would do in
        // production shortly after a bulk load, so benchmarks measure realistic query plans.
        await using (var conn = new NpgsqlConnection(container.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ANALYZE";
            await cmd.ExecuteNonQueryAsync();
        }

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
