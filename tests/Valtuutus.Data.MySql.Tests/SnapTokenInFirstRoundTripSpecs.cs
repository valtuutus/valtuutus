using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// A Check without a caller token resolves the latest token inside its first round trip
    /// instead of a dedicated one, and every round trip of that request still runs against that one
    /// token, so a write committed mid-request stays invisible to it.
    /// </summary>
    public abstract class SnapTokenInFirstRoundTripSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
    {
        // Neither doc.owner nor group.member can take the 2-hop join fast path (group.member has two
        // userset targets), so a check through a group needs a first wave (direct membership + the
        // doc's group usersets) and a second, dependent, batched wave (membership in each group found).
        private const string Schema = """
            entity user {}
            entity team {
                relation member @user;
            }
            entity org {
                relation member @user;
            }
            entity group {
                relation member @user @team#member @org#member;
            }
            entity doc {
                relation owner @user @group#member;
                permission view := owner;
            }
            """;

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task Tokenless_check_resolves_the_token_in_its_only_round_trip()
        {
            var written = await Write(new RelationTuple("doc", "d1", "owner", "user", "alice"));

            var counter = new RoundTripCounter();
            await using var sp = BuildServices(counter);
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

            var result = await engine.Check(new CheckRequest("doc", "d1", "view", "user", "alice"), default);

            result.Allowed.Should().BeTrue();
            result.SnapToken.Should().Be(written, "the engine reports the token its first round trip resolved");
            // The direct-membership answer and the token arrive together; a separate
            // GetLatestSnapToken round trip would have reached the counting reader.
            counter.Count.Should().Be(1);
        }

        [Fact]
        public async Task Write_committed_between_waves_is_invisible_to_the_running_check()
        {
            await Write(new RelationTuple("doc", "d1", "owner", "group", "g1", "member"));

            var counter = new RoundTripCounter();
            var wroteMidRequest = false;
            await using var sp = BuildServices(counter, beforeRoundTrip: async roundTrip =>
            {
                if (roundTrip != 2) return;
                // Wave 1 already pinned the token; this grant commits after it, before wave 2 runs.
                await Task.Delay(5);
                await Write(new RelationTuple("group", "g1", "member", "user", "alice"));
                wroteMidRequest = true;
            });
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

            var duringWrite = (await engine.Check(new CheckRequest("doc", "d1", "view", "user", "alice"), default)).Allowed;
            var afterWrite = (await engine.Check(new CheckRequest("doc", "d1", "view", "user", "alice"), default)).Allowed;

            wroteMidRequest.Should().BeTrue("the check must have needed a second wave for this spec to mean anything");
            duringWrite.Should().BeFalse("the grant committed after the request's token was resolved");
            afterWrite.Should().BeTrue("a new request resolves a new token that includes the grant");
        }

        [Fact]
        public async Task Explicit_token_requests_keep_binding_that_token()
        {
            var before = await Write(new RelationTuple("doc", "d1", "owner", "user", "bob"));
            await Write(new RelationTuple("doc", "d1", "owner", "user", "alice"));

            var counter = new RoundTripCounter();
            await using var sp = BuildServices(counter);
            await using var scope = sp.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

            // A tokenless check first leaves its resolved token in the pooled connection's session
            // variable; the explicit-token request must still see only its own snapshot.
            (await engine.Check(new CheckRequest("doc", "d1", "view", "user", "alice"), default)).Allowed.Should().BeTrue();
            (await engine.Check(new CheckRequest("doc", "d1", "view", "user", "alice", snapToken: before), default)).Allowed
                .Should().BeFalse();
        }

        private ServiceProvider BuildServices(RoundTripCounter counter, Func<int, Task>? beforeRoundTrip = null)
        {
            counter.BeforeRoundTrip = beforeRoundTrip;
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);
            services.AddSingleton(counter);
            services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
                new CountingReaderProvider(ActivatorUtilities.CreateInstance<MySqlDataReaderProvider>(sp), counter)));
            services.Replace(ServiceDescriptor.Singleton<IRelationalBatchOps>(sp =>
                new CountingBatchOps(
                    new MySqlBatchOps(sp.GetRequiredService<DbConnectionFactory>(),
                        sp.GetRequiredService<ValtuutusDataOptions>(),
                        sp.GetRequiredService<ValtuutusMySqlOptions>()),
                    counter)));
            return services.BuildServiceProvider();
        }

        private async Task<SnapToken> Write(RelationTuple tuple)
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options);
            await using var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write([tuple], [], default);
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class SnapTokenInFirstRoundTripSpecs(MySqlFixture fixture) : SnapTokenInFirstRoundTripSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class SnapTokenInFirstRoundTripSpecs(MariaDbFixture fixture) : SnapTokenInFirstRoundTripSpecsBase(fixture);
}
