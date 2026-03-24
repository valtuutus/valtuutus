using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data;
using Testcontainers.PostgreSql;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Postgres;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PostgresCheckBenchmarks
{
    private readonly PostgreSqlContainer _pgContainer = new PostgreSqlBuilder()
        .WithUsername("Valtuutus")
        .WithPassword("Valtuutus123")
        .WithDatabase("Valtuutus")
        .WithName($"pg-check-benchmarks-{Guid.NewGuid()}")
        .Build();

    private ServiceProvider _serviceProvider = null!;
    private ICheckEngine _checkEngine = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        await _pgContainer.StartAsync();
        IDbConnection DbFactory() => new NpgsqlConnection(_pgContainer.GetConnectionString());
        var pgAssembly = typeof(ValtuutusPostgresOptions).Assembly;
        (_serviceProvider, _checkEngine, _) = await CommonSetup.MigrateAndSeed(
            sc => sc.AddPostgres(_ => DbFactory),
            Seeder.Seeder.GenerateData(),
            pgAssembly);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Check_Simple")]
    public async Task<bool> Check_Simple()
    {
        return await _checkEngine.Check(new()
        {
            Permission = "admin",
            EntityType = "organization",
            EntityId = "5171869f-b4e4-ca9a-b800-5e1dab069a26",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
        }, CancellationToken.None);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Check_Complex")]
    public async Task<bool> Check_Complex()
    {
        return await _checkEngine.Check(new()
        {
            Permission = "edit",
            EntityType = "project",
            EntityId = "e4010d7b-cea1-94c6-2232-e1f9ae557272",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
        }, CancellationToken.None);
    }

    [Benchmark(Baseline = true), BenchmarkCategory("SubjectPermission")]
    public async Task<Dictionary<string, bool>> SubjectPermission()
    {
        return await _checkEngine.SubjectPermission(new()
        {
            EntityType = "project",
            EntityId = "e4010d7b-cea1-94c6-2232-e1f9ae557272",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
        }, CancellationToken.None);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _pgContainer.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }
}
