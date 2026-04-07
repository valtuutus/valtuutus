using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.InMemory;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class InMemoryBenchmarks : BenchmarkBase
{
    private ServiceProvider _serviceProvider = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        (_serviceProvider, _checkEngine, _lookupEntityEngine) = await CommonSetup.Seed(
            sc => sc.AddInMemory(),
            Seeder.Seeder.GenerateData());
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _serviceProvider.DisposeAsync();
    }
}
