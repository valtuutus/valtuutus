using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data.InMemory;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class InMemoryBenchmarks
{
    private ServiceProvider _serviceProvider = null!;
    private ICheckEngine _checkEngine = null!;
    private ILookupEntityEngine _lookupEntityEngine = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        (_serviceProvider, _checkEngine, _lookupEntityEngine) = await CommonSetup.Seed(
            sc => sc.AddInMemory(),
            Seeder.Seeder.GenerateData());
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
        await _serviceProvider.DisposeAsync();
    }
}
