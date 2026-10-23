using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MariaDb;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data.MySql;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class MariaDbBenchmarks : BenchmarkBase
{
    // See SqlServerBenchmarks: GlobalSetup/GlobalCleanup fire once per benchmark case (once per
    // method), not once per class, regardless of toolchain. Lazy<Task<T>> makes the
    // container+migrate+seed run exactly once across all instances.
    private static readonly Lazy<Task<(ServiceProvider ServiceProvider, ICheckEngine CheckEngine, ILookupEntityEngine LookupEntityEngine, ILookupSubjectEngine LookupSubjectEngine)>> Shared = new(InitializeAsync);

    private static async Task<(ServiceProvider, ICheckEngine, ILookupEntityEngine, ILookupSubjectEngine)> InitializeAsync()
    {
        var container = new MariaDbBuilder("mariadb:10.11")
            .WithUsername("root")
            .WithPassword("Valtuutus123")
            .WithDatabase("valtuutus")
            .WithName($"mariadb-benchmarks-{Guid.NewGuid()}")
            .Build();
        await container.StartAsync();
        var (serviceProvider, checkEngine, lookupEntityEngine, lookupSubjectEngine) =
            await MySqlBenchmarkSetup.MigrateSeedAndAnalyze(container.GetConnectionString(), MySqlServerType.MariaDb);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            container.DisposeAsync().AsTask().GetAwaiter().GetResult();
            serviceProvider.Dispose();
        };

        return (serviceProvider, checkEngine, lookupEntityEngine, lookupSubjectEngine);
    }

    [GlobalSetup]
    public async Task Setup()
    {
        (_, _checkEngine, _lookupEntityEngine, _lookupSubjectEngine) = await Shared.Value;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        // No-op: shared container/provider are torn down once via ProcessExit above.
    }
}
