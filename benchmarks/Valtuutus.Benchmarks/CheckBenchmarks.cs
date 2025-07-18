using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.InMemory;
using Valtuutus.Data.Postgres;
using Valtuutus.Data.SqlServer;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class CheckBenchmarks
{
    private readonly PostgreSqlContainer _pgContainer = new PostgreSqlBuilder()
        .WithUsername("Valtuutus")
        .WithPassword("Valtuutus123")
        .WithDatabase("Valtuutus")
        .WithName($"pg-benchmarks-{Guid.NewGuid()}")
        .Build();
    
    private readonly MsSqlContainer _msSqlContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
        .WithPassword("Valtuutus123!")
        .WithName($"mssql-benchmarks-{Guid.NewGuid()}")
        .Build();


    private ServiceProvider _inMemoryServiceProvider = null!;
    private ICheckEngine _inMemoryCheckEngine = null!;
    private ServiceProvider _pgServiceProvider = null!;
    private ICheckEngine _pgCheckEngine = null!;
    private ServiceProvider _mssqlServiceProvider = null!;
    private ICheckEngine _mssqlCheckEngine = null!;

    [GlobalSetup]
    public void Setup()
    {
        var relAndAttributes = Seeder.Seeder.GenerateData();
        var a = SetupPg(relAndAttributes);
        var b = SetupMssql(relAndAttributes);
        var c = SetupInMemory(relAndAttributes);
        Task.WhenAll(a, b, c).GetAwaiter().GetResult();
    }

    private async Task SetupInMemory((List<RelationTuple> Relations, List<AttributeTuple> Attributes) relAndAttributes)
    {
        (_inMemoryServiceProvider, _inMemoryCheckEngine, _) = await CommonSetup.Seed(
            (sc) => sc.AddInMemory(),
            relAndAttributes);
    }

    private async Task SetupPg((List<RelationTuple> Relations, List<AttributeTuple> Attributes) relAndAttributes)
    {
        await _pgContainer.StartAsync();
        IDbConnection DbFactory() => new NpgsqlConnection(_pgContainer.GetConnectionString());
        var pgAssembly = typeof(ValtuutusPostgresOptions).Assembly;
        (_pgServiceProvider, _pgCheckEngine, _) = await CommonSetup.MigrateAndSeed(
                (sc) => sc.AddPostgres(_ => DbFactory),
                relAndAttributes,
                pgAssembly);
    }

    private async Task SetupMssql((List<RelationTuple> Relations, List<AttributeTuple> Attributes) relAndAttributes)
    {
        await _msSqlContainer.StartAsync();
        IDbConnection DbFactory() => new SqlConnection(_msSqlContainer.GetConnectionString());
        var mssqlAssembly = typeof(ValtuutusSqlServerOptions).Assembly;
        (_mssqlServiceProvider, _mssqlCheckEngine, _) = await CommonSetup.MigrateAndSeed(
            (sc) => sc.AddSqlServer(_ => DbFactory),
            relAndAttributes,
            mssqlAssembly);
    }
    
    [Benchmark(Baseline = true), BenchmarkCategory("Simple")]
    public async Task<bool> CheckRequest_Simple_InMemory()
    {
        return await _inMemoryCheckEngine.Check(new ()
        {
            Permission = "admin",
            EntityType = "organization",
            EntityId = "5171869f-b4e4-ca9a-b800-5e1dab069a26",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
            
        }, CancellationToken.None);
    }
    
    [Benchmark, BenchmarkCategory("Simple")]
    public async Task<bool> CheckRequest_Simple_Pg()
    {
        return await _pgCheckEngine.Check(new ()
        {
            Permission = "admin",
            EntityType = "organization",
            EntityId = "5171869f-b4e4-ca9a-b800-5e1dab069a26",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
            
        }, CancellationToken.None);
    }
    
    [Benchmark, BenchmarkCategory("Simple")]
    public async Task<bool> CheckRequest_Simple_Mssql()
    {
        return await _mssqlCheckEngine.Check(new ()
        {
            Permission = "admin",
            EntityType = "organization",
            EntityId = "5171869f-b4e4-ca9a-b800-5e1dab069a26",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
            
        }, CancellationToken.None);
    }
    
    [Benchmark(Baseline = true), BenchmarkCategory("Complex")]
    public async Task<bool> CheckRequest_Complex_InMemory()
    {
        return await _inMemoryCheckEngine.Check(new ()
        {
            Permission = "edit",
            EntityType = "project",
            EntityId = "e4010d7b-cea1-94c6-2232-e1f9ae557272",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
        }, CancellationToken.None);
    }
    
    [Benchmark, BenchmarkCategory("Complex")]
    public async Task<bool> CheckRequest_Complex_Pg()
    {
        return await _pgCheckEngine.Check(new ()
        {
            Permission = "edit",
            EntityType = "project",
            EntityId = "e4010d7b-cea1-94c6-2232-e1f9ae557272",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
        }, CancellationToken.None);
    }
    
    
    [Benchmark, BenchmarkCategory("Complex")]
    public async Task<bool> CheckRequest_Complex_Mssql()
    {
        return await _mssqlCheckEngine.Check(new ()
        {
            Permission = "edit",
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
        await _pgServiceProvider.DisposeAsync();
        await _msSqlContainer.DisposeAsync();
        await _mssqlServiceProvider.DisposeAsync();
        await _inMemoryServiceProvider.DisposeAsync();
    }
    
}