using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using Testcontainers.MsSql;
using Valtuutus.Core;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data.SqlServer;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SqlServerLookupBenchmarks
{
    private readonly MsSqlContainer _msSqlContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
        .WithPassword("Valtuutus123!")
        .WithName($"mssql-lookup-benchmarks-{Guid.NewGuid()}")
        .Build();

    private ServiceProvider _serviceProvider = null!;
    private ILookupEntityEngine _lookupEntityEngine = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        await _msSqlContainer.StartAsync();
        IDbConnection DbFactory() => new SqlConnection(_msSqlContainer.GetConnectionString());
        var mssqlAssembly = typeof(ValtuutusSqlServerOptions).Assembly;
        (_serviceProvider, _, _lookupEntityEngine) = await CommonSetup.MigrateAndSeed(
            sc => sc.AddSqlServer(_ => DbFactory),
            Seeder.Seeder.GenerateData(),
            mssqlAssembly);
    }

    [Benchmark(Baseline = true)]
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
