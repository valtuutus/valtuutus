using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data.InMemory;

namespace Valtuutus.Benchmarks;

/// <summary>
/// Short-run benchmarks targeting specific optimization scenarios that don't appear
/// in the main InMemoryBenchmarks suite. Run with --filter '*Validation*'.
/// Results are recorded per-commit to validate each Tier 1 optimization.
/// </summary>
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class OptimizationValidationBenchmarks
{
    // Fixed IDs matching seeded data — see Valtuutus.Seeder/Seeder.cs
    private const string ReflexiveGroupId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string BenchmarkUserId  = "3fca4119-3bda-4370-13cd-a3d317459c73";
    private const string BenchmarkProjectId = "e4010d7b-cea1-94c6-2232-e1f9ae557272";

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

    /// <summary>
    /// Reflexive fast-path: entity+relation == subject (group:X#member asking about group:X#member).
    /// Without optimisation: CheckRelation fetches DB tuples before finding the match.
    /// With optimisation: returns true at CheckInternal entry, zero DB calls.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("Reflexive")]
    public async Task<bool> Check_Reflexive()
    {
        return await _checkEngine.Check(new CheckRequest
        {
            EntityType = "group",
            EntityId = ReflexiveGroupId,
            Permission = "member",
            SubjectType = "group",
            SubjectId = ReflexiveGroupId,
            SubjectRelation = "member"
        }, CancellationToken.None);
    }

    /// <summary>
    /// Convergent-paths dedup: cache_test permission has org.admin twice.
    /// Without optimisation: LookupEntityInternal(org, admin) is resolved twice.
    /// With VisitsMap: second traversal is skipped entirely.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupDedup")]
    public async Task<HashSet<string>> LookupEntity_ConvergentPaths()
    {
        return await _lookupEntityEngine.LookupEntity(new LookupEntityRequest
        {
            Permission = "cache_test",
            EntityType = "project",
            SubjectType = "user",
            SubjectId = BenchmarkUserId
        }, CancellationToken.None);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _serviceProvider.DisposeAsync();
    }
}
