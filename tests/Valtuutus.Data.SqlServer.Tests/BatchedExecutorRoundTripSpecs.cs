using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

/// <summary>
/// SqlServer counterpart of Valtuutus.Data.Postgres.Tests.BatchedExecutorRoundTripSpecs — the one
/// claim only a real SqlServer can prove: BatchedPhysicalExecutor genuinely collapses a wave's
/// several batchable ops into fewer physical round trips than running them individually, and
/// produces the identical answer either way.
///
/// The schema's `view` union has FOUR sibling children that can never fuse into one op: a bare
/// relation ref (`owner`), a tuple-to-userset fast-path ref (`team_link.member`), a bare
/// attribute ref (`public`), and a reference to another PERMISSION
/// (`unfusable_sub_permission`, itself `owner and public`). RelationalPlanRewriter's
/// full-fusion pass requires every child of the union to resolve to a recognizable leaf kind
/// (direct relation, attribute, TTU fast path, or an already-fused sibling group) — a reference
/// to another permission resolves to `RelationType.Permission`, which none of those checks
/// recognize, so the pass can't fully fuse this union. Partial fusion (which only groups >= 2
/// same-kind siblings) also can't fire here, since `owner` and `public` each appear only once at
/// this level. The result: `owner`, `team_link.member`, `public`, and the sub-permission's own
/// resolved op each land in the executor as separate leaf ops in the same wave — exactly the
/// shape BatchedPhysicalExecutor's DbBatch packing targets, as opposed to a union whose children
/// the rewriter CAN fully fuse, where fusion already collapses everything to a single op at plan
/// rewrite time, before the physical executor ever sees more than one op to batch.
///
/// Forcing the "one round trip per op" comparison can't go through DefaultPhysicalExecutor
/// directly — it's internal to Valtuutus.Core with no InternalsVisibleTo grant to this test
/// assembly. Instead this toggles whether an IRelationalBatchOps is registered at all:
/// BatchedPhysicalExecutor now receives its batch capability injected (constructed once per
/// Schema by AddSqlServer's Func&lt;Schema, IPhysicalExecutor&gt; factory, which resolves
/// IRelationalBatchOps via IServiceProvider.GetService — optional, not required), so removing
/// the registration entirely reproduces the exact same SubmitAllIndividually fallback
/// BatchedPhysicalExecutor takes when a provider has no batch implementation. Same code path,
/// no internals access required.
/// </summary>
[Collection("SqlServerSpec")]
public sealed class BatchedExecutorRoundTripSpecs : IAsyncLifetime
{
    private const string Schema = """
        entity user {}
        entity team {
            relation member @user;
        }
        entity doc {
            relation owner @user;
            relation team_link @team;
            attribute public bool;
            permission unfusable_sub_permission := owner and public;
            permission view := owner or team_link.member or public or unfusable_sub_permission;
        }
        """;

    public BatchedExecutorRoundTripSpecs(SqlServerFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        Fixture = fixture;
        Output = output;
    }

    private SqlServerFixture Fixture { get; }
    private Xunit.Abstractions.ITestOutputHelper Output { get; }

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task BatchedExecutor_UsesFewerRoundTrips_ThanIndividualExecution_ForSameWave()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;

        // Seed once via a plain (uncounted) container so both measured runs read the identical
        // snapshot — and capture the write's own SnapToken so neither measured run needs its own
        // GetLatestSnapToken round trip (which would be identical in both runs anyway, but this
        // keeps the counts clean).
        SnapToken snapToken;
        {
            var seedServices = new ServiceCollection().AddValtuutusCore(Schema);
            seedServices.AddSqlServer(_ => dbFactory).AddConcurrentQueryLimit(3);
            await using var seedProvider = seedServices.BuildServiceProvider();
            await using var seedScope = seedProvider.CreateAsyncScope();
            var writer = seedScope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
            snapToken = await writer.Write(
                [new RelationTuple("doc", "d1", "owner", "user", "alice")],
                [],
                default);
        }

        var request = new CheckRequest("doc", "d1", "view", "user", "alice", snapToken: snapToken);

        var (batchedResult, batchedRoundTrips) = await RunCheck(dbFactory, request, batching: true);
        var (individualResult, individualRoundTrips) = await RunCheck(dbFactory, request, batching: false);

        Output.WriteLine($"batched={batchedRoundTrips}, individual={individualRoundTrips}");

        // Same answer either way (alice is doc:d1's direct owner) — the round-trip mechanics must
        // never change what Check() returns.
        batchedResult.Should().BeTrue();
        individualResult.Should().Be(batchedResult);

        batchedRoundTrips.Should().BeLessThan(individualRoundTrips,
            $"batched={batchedRoundTrips}, individual={individualRoundTrips}");
    }

    private static async Task<(bool Result, int RoundTrips)> RunCheck(DbConnectionFactory dbFactory, CheckRequest request, bool batching)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddSqlServer(_ => dbFactory).AddConcurrentQueryLimit(3);
        services.AddValtuutusCheckV2();

        var counter = new RoundTripCounter();
        services.AddSingleton(counter);

        // AddSqlServer already registered IDataReaderProvider as a plain SqlServerDataReaderProvider
        // (scoped) — Replace it with a counting decorator that wraps a freshly-constructed real
        // one, resolved via ActivatorUtilities so it still gets AddSqlServer/AddDbSetup's
        // DbConnectionFactory/ValtuutusDataOptions/IValtuutusDbOptions registrations. Used in
        // both runs: the batching run's savings show up as calls that never reach this decorator,
        // routed instead through CountingBatchOps below.
        services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
        {
            var real = ActivatorUtilities.CreateInstance<SqlServerDataReaderProvider>(sp);
            return new CountingReaderProvider(real, sp.GetRequiredService<RoundTripCounter>());
        }));

        if (batching)
        {
            // AddSqlServer already registered a real IRelationalBatchOps singleton
            // (SqlServerBatchOps) — Replace it with a counting decorator built the same way, so
            // ExecuteBatchAsync calls land in the same counter as the reader's individual calls
            // above.
            services.Replace(ServiceDescriptor.Singleton<IRelationalBatchOps>(sp =>
                new CountingBatchOps(
                    ActivatorUtilities.CreateInstance<SqlServerBatchOps>(sp),
                    sp.GetRequiredService<RoundTripCounter>())));
        }
        else
        {
            // No IRelationalBatchOps registered at all: BatchedPhysicalExecutor's injected
            // batchOps is null, so every op falls back to SubmitAllIndividually — the individual
            // round trips land on the counted reader above instead.
            services.RemoveAll<IRelationalBatchOps>();
        }

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
        var result = await engine.Check(request, default);
        return (result, counter.Count);
    }
}
