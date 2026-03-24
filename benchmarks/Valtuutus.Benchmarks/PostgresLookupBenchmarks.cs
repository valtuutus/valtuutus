using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Data;
using Testcontainers.PostgreSql;
using Valtuutus.Core;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data.Postgres;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class PostgresLookupBenchmarks
{
    private readonly PostgreSqlContainer _pgContainer = new PostgreSqlBuilder()
        .WithUsername("Valtuutus")
        .WithPassword("Valtuutus123")
        .WithDatabase("Valtuutus")
        .WithName($"pg-lookup-benchmarks-{Guid.NewGuid()}")
        .Build();

    private ServiceProvider _serviceProvider = null!;
    private ILookupEntityEngine _lookupEntityEngine = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        await _pgContainer.StartAsync();
        IDbConnection DbFactory() => new NpgsqlConnection(_pgContainer.GetConnectionString());
        var pgAssembly = typeof(ValtuutusPostgresOptions).Assembly;
        (_serviceProvider, _, _lookupEntityEngine) = await CommonSetup.MigrateAndSeed(
            sc => sc.AddPostgres(_ => DbFactory),
            Seeder.Seeder.GenerateData(),
            pgAssembly);
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
        await _pgContainer.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }
}
