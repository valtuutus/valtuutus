using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data.InMemory;
using Valtuutus.Data.Postgres;
using Valtuutus.Data.SqlServer;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class LookupBenchmarks
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

    private ServiceProvider _pgServiceProvider = null!;
    private ILookupEntityEngine _pgLookupEntityEngine = null!;
    private ServiceProvider _mssqlServiceProvider = null!;
    private ILookupEntityEngine _msSqlLookupEntityEngine = null!;
    private ServiceProvider _inMemoryServiceProvider = null!;
    private ILookupEntityEngine _inMemoryLookupEntityEngine = null!;
    
    [GlobalSetup]
    public void Setup()
    {
        var relAndAttributes = Seeder.Seeder.GenerateData();
        var a = SetupPg(relAndAttributes);
        var b = SetupMssql(relAndAttributes);
        var c = SetupInMemory(relAndAttributes);
        Task.WhenAll(a, b, c).GetAwaiter().GetResult();
    }
    
    

    private async Task SetupPg((List<RelationTuple> Relations, List<AttributeTuple> Attributes) relAndAttributes)
    {
        await _pgContainer.StartAsync();
        IDbConnection DbFactory() => new NpgsqlConnection(_pgContainer.GetConnectionString());
        var pgAssembly = typeof(ValtuutusPostgresOptions).Assembly;
        (_pgServiceProvider, _, _pgLookupEntityEngine) = await CommonSetup.MigrateAndSeed(
                (sc) => sc.AddPostgres(_ => DbFactory),
                relAndAttributes,
                pgAssembly);
    }

    private async Task SetupMssql((List<RelationTuple> Relations, List<AttributeTuple> Attributes) relAndAttributes)
    {
        await _msSqlContainer.StartAsync();
        IDbConnection DbFactory() => new SqlConnection(_msSqlContainer.GetConnectionString());
        var mssqlAssembly = typeof(ValtuutusSqlServerOptions).Assembly;
        (_mssqlServiceProvider, _, _msSqlLookupEntityEngine) = await CommonSetup.MigrateAndSeed(
            (sc) => sc.AddSqlServer(_ => DbFactory),
            relAndAttributes,
            mssqlAssembly);
    }
    
    private async Task SetupInMemory((List<RelationTuple> Relations, List<AttributeTuple> Attributes) relAndAttributes)
    {
        (_inMemoryServiceProvider, _, _inMemoryLookupEntityEngine) = await CommonSetup.Seed(
            (sc) => sc.AddInMemory(),
            relAndAttributes);
    }

    [Benchmark]
    public async Task<HashSet<string>> LookupEntity_InMemory()
    {
        return await _inMemoryLookupEntityEngine.LookupEntity(new ()
        {
            Permission = "edit",
            EntityType = "project",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
        }, CancellationToken.None);
    }
    
    [Benchmark]
    public async Task<HashSet<string>> LookupEntity_Mssql()
    {
        return await _msSqlLookupEntityEngine.LookupEntity(new ()
        {
            Permission = "edit",
            EntityType = "project",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
        }, CancellationToken.None);
    }
    
    [Benchmark]
    public async Task<HashSet<string>> LookupEntity_Pg()
    {
        return await _pgLookupEntityEngine.LookupEntity(new ()
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
        await _pgContainer.DisposeAsync();
        await _pgServiceProvider.DisposeAsync();
        await _msSqlContainer.DisposeAsync();
        await _mssqlServiceProvider.DisposeAsync();
        await _inMemoryServiceProvider.DisposeAsync();
    }
    
}