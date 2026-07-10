using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data;
using Testcontainers.PostgreSql;
using Valtuutus.Data.Postgres;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PostgresBenchmarks : BenchmarkBase
{
    private readonly PostgreSqlContainer _pgContainer = new PostgreSqlBuilder()
        .WithUsername("Valtuutus")
        .WithPassword("Valtuutus123")
        .WithDatabase("Valtuutus")
        .WithName($"pg-benchmarks-{Guid.NewGuid()}")
        .Build();

    private ServiceProvider _serviceProvider = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        await _pgContainer.StartAsync();
        IDbConnection DbFactory() => new NpgsqlConnection(_pgContainer.GetConnectionString());
        var pgAssembly = typeof(ValtuutusPostgresOptions).Assembly;
        (_serviceProvider, _checkEngine, _lookupEntityEngine, _lookupSubjectEngine) = await CommonSetup.MigrateAndSeed(
            sc => sc.AddPostgres(_ => DbFactory),
            Seeder.Seeder.GenerateData(),
            pgAssembly);

        // The bulk seed write leaves relation_tuples/attributes with zero planner statistics
        // (autovacuum's autoanalyze hasn't run yet). Without stats, Postgres defaults to ~1-row
        // cost estimates everywhere and picks nested-loop plans over the wrong index for the
        // GetRelationsJoined(ByEntityIds) two-hop-collapse queries — 1000x+ slower than the
        // steady-state plan it picks once stats exist. Mirrors what autovacuum would do in
        // production shortly after a bulk load, so benchmarks measure realistic query plans.
        await using (var conn = new NpgsqlConnection(_pgContainer.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ANALYZE";
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _pgContainer.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }
}
