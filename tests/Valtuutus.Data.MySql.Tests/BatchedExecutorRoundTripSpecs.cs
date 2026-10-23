using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;
using Xunit.Abstractions;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// BatchedPhysicalExecutor collapses a wave's several batchable ops into fewer physical round
    /// trips than running them individually, and produces the identical answer either way.
    ///
    /// The schema's `view` union has four sibling children that can never fuse into one op: a bare
    /// relation ref (`owner`), a tuple-to-userset fast-path ref (`team_link.member`), a bare
    /// attribute ref (`public`), and a reference to another permission
    /// (`unfusable_sub_permission`). Full fusion needs every child of the union to resolve to a
    /// recognizable leaf kind, and a permission reference is none of them. Partial fusion only
    /// groups two or more same-kind siblings, and `owner` and `public` each appear once at this
    /// level. So the four children land in the executor as separate leaf ops of the same wave,
    /// the shape DbBatch packing targets.
    ///
    /// The individual run removes the IRelationalBatchOps registration: BatchedPhysicalExecutor
    /// resolves it optionally, and without one it submits every op individually, the same path a
    /// provider without batch support takes.
    /// </summary>
    public abstract class BatchedExecutorRoundTripSpecsBase(MySqlFixtureBase fixture, ITestOutputHelper output)
        : IAsyncLifetime
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

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task BatchedExecutor_UsesFewerRoundTrips_ThanIndividualExecution_ForSameWave()
        {
            // Seeded through an uncounted container; both measured runs read the write's own token,
            // so neither needs to resolve one.
            SnapToken snapToken;
            {
                var seedServices = new ServiceCollection().AddValtuutusCore(Schema);
                seedServices.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);
                await using var seedProvider = seedServices.BuildServiceProvider();
                await using var seedScope = seedProvider.CreateAsyncScope();
                var writer = seedScope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
                snapToken = await writer.Write(
                    [new RelationTuple("doc", "d1", "owner", "user", "alice")],
                    [],
                    default);
            }

            var request = new CheckRequest("doc", "d1", "view", "user", "alice", snapToken: snapToken);

            var (batchedResult, batchedRoundTrips) = await RunCheck(request, batching: true);
            var (individualResult, individualRoundTrips) = await RunCheck(request, batching: false);

            output.WriteLine($"batched={batchedRoundTrips}, individual={individualRoundTrips}");

            // alice is doc:d1's direct owner; how the round trips are packed never changes the answer.
            batchedResult.Should().BeTrue();
            individualResult.Should().Be(batchedResult);

            batchedRoundTrips.Should().BeLessThan(individualRoundTrips,
                $"batched={batchedRoundTrips}, individual={individualRoundTrips}");
        }

        private async Task<(bool Result, int RoundTrips)> RunCheck(CheckRequest request, bool batching)
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);

            var counter = new RoundTripCounter();
            services.AddSingleton(counter);

            // The counting reader takes every individual call in both runs.
            services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
            {
                var real = ActivatorUtilities.CreateInstance<MySqlDataReaderProvider>(sp);
                return new CountingReaderProvider(real, sp.GetRequiredService<RoundTripCounter>());
            }));

            if (batching)
            {
                services.Replace(ServiceDescriptor.Singleton<IRelationalBatchOps>(sp =>
                    new CountingBatchOps(
                        new MySqlBatchOps(sp.GetRequiredService<DbConnectionFactory>(),
                            sp.GetRequiredService<ValtuutusDataOptions>(),
                            sp.GetRequiredService<ValtuutusMySqlOptions>()),
                        sp.GetRequiredService<RoundTripCounter>())));
            }
            else
            {
                services.RemoveAll<IRelationalBatchOps>();
            }

            await using var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
            var result = (await engine.Check(request, default)).Allowed;
            return (result, counter.Count);
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class BatchedExecutorRoundTripSpecs(MySqlFixture fixture, ITestOutputHelper output)
        : BatchedExecutorRoundTripSpecsBase(fixture, output);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class BatchedExecutorRoundTripSpecs(MariaDbFixture fixture, ITestOutputHelper output)
        : BatchedExecutorRoundTripSpecsBase(fixture, output);
}
