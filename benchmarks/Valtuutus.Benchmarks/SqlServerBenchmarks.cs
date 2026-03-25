using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using Testcontainers.MsSql;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data.SqlServer;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SqlServerBenchmarks
{
    private readonly MsSqlContainer _msSqlContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
        .WithPassword("Valtuutus123!")
        .WithName($"mssql-benchmarks-{Guid.NewGuid()}")
        .Build();

    private ServiceProvider _serviceProvider = null!;
    private ICheckEngine _checkEngine = null!;
    private ILookupEntityEngine _lookupEntityEngine = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        await _msSqlContainer.StartAsync();
        IDbConnection DbFactory() => new SqlConnection(_msSqlContainer.GetConnectionString());
        var mssqlAssembly = typeof(ValtuutusSqlServerOptions).Assembly;
        (_serviceProvider, _checkEngine, _lookupEntityEngine) = await CommonSetup.MigrateAndSeed(
            sc => sc.AddSqlServer(_ => DbFactory),
            Seeder.Seeder.GenerateData(),
            mssqlAssembly);
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

    [Benchmark(Baseline = true), BenchmarkCategory("LookupEntity")]
    public async Task<HashSet<string>> LookupEntity()
    {
        return await _lookupEntityEngine.LookupEntity(new()
        {
            Permission = "edit",
            EntityType = "project",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
        }, CancellationToken.None);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _msSqlContainer.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }
}
