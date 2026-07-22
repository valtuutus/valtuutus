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
/// produces the identical answer either way. Same schema/scenario/technique as the Postgres
/// version — see that file's doc comment for full rationale (three different op kinds in one
/// Union, so RelationalPlanRewriter's sibling fusion never fuses any of them away at
/// plan-rewrite time; toggling IRelationalBatchOps presence via a counting decorator instead of
/// touching DefaultPhysicalExecutor directly, since that type is internal to Valtuutus.Core with
/// no InternalsVisibleTo grant to this test assembly).
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
            permission view := owner or team_link.member or public;
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
