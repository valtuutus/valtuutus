using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

/// <summary>
/// DbBatch-Task 8, Step 3: the one claim in the whole R4 plan that only real Postgres can prove —
/// BatchedPhysicalExecutor genuinely collapses a wave's several batchable ops into fewer physical
/// round trips than running them individually, and produces the identical answer either way.
///
/// The schema's `view := owner or team_link.member or public` union has three sibling children
/// of three DIFFERENT op kinds (HasDirectRelation, TtuFastPath, HasTrueBoolAttribute) — each
/// appears only once, so neither GroupSiblingDirectRelations nor GroupSiblingAttributeTruth (both
/// same-kind, ≥2 siblings) fires at compile time. All three land in the executor as separate
/// leaf ops in the same wave, which is exactly the shape BatchedPhysicalExecutor's DbBatch
/// packing targets — as opposed to reusing the R4 attribute fixture from Step 2, where the fusion
/// already collapses to a single op at PLAN COMPILE time, before the physical executor ever sees
/// more than one op to batch.
///
/// Forcing the "one round trip per op" comparison can't go through DefaultPhysicalExecutor
/// directly — it's internal to Valtuutus.Core with no InternalsVisibleTo grant to this test
/// assembly. Instead this toggles whether the registered IDataReaderProvider also implements
/// IRelationalBatchOps: BatchedPhysicalExecutor (which AddPostgres always registers) checks for
/// that capability at runtime and falls back to SubmitAllIndividually — the exact same per-op
/// PhysicalOpRunner path DefaultPhysicalExecutor itself uses — when it's absent. Same code path,
/// no internals access required.
/// </summary>
[Collection("PostgreSqlSpec")]
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

    public BatchedExecutorRoundTripSpecs(PostgresFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        Fixture = fixture;
        Output = output;
    }

    private PostgresFixture Fixture { get; }
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
            seedServices.AddPostgres(_ => dbFactory).AddConcurrentQueryLimit(3);
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
        services.AddPostgres(_ => dbFactory).AddConcurrentQueryLimit(3);
        services.AddValtuutusCheckV2();
        // AddPostgres already registered IDataReaderProvider as a plain PostgresDataReaderProvider
        // (scoped) — Replace it with a counting decorator that wraps a freshly-constructed real
        // one, resolved via ActivatorUtilities so it still gets AddPostgres/AddDbSetup's
        // DbConnectionFactory/ValtuutusDataOptions/ValtuutusPostgresOptions registrations.
        services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
        {
            var real = ActivatorUtilities.CreateInstance<Postgres.PostgresDataReaderProvider>(sp);
            return batching
                ? new BatchingCountingReader(real)
                : (IDataReaderProvider)new NonBatchingCountingReader(real);
        }));

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
        var result = await engine.Check(request, default);
        // Same scope, so this resolves the exact decorator instance the engine just used.
        var counting = (RoundTripCountingReaderBase)scope.ServiceProvider.GetRequiredService<IDataReaderProvider>();
        return (result, counting.RoundTripCount);
    }
}
