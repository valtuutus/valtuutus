using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.MySql.Tests
{
    public abstract class TracingSpecsBase(MySqlFixtureBase fixture, string dbSystem) : IAsyncLifetime
    {
        private const string Schema = """
            entity user {}
            entity doc {
                relation owner @user;
                relation editor @user;
                permission view := owner or editor;
            }
            """;

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task Tokenless_check_resolves_its_token_in_the_batched_round_trip_span()
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options);
            await using var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            var written = await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>()
                .Write([new RelationTuple("doc", "d1", "editor", "user", "alice")], [], default);
            var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

            using var spans = new SpanCollector();
            await engine.Check(new CheckRequest("doc", "d1", "view", "user", "alice"), default);

            var check = spans.Named("valtuutus.check").Should().ContainSingle().Subject;
            check.GetTagItem("valtuutus.snap_token").Should().Be(written.Value);
            check.GetTagItem("valtuutus.result").Should().Be(true);
            check.GetTagItem("valtuutus.round_trips").Should().Be(1);
            var roundTrip = (await spans.WaitForChildren(check, 1)).Should().ContainSingle().Subject;
            roundTrip.OperationName.Should().Be("valtuutus.round_trip");
            roundTrip.GetTagItem("db.system.name").Should().Be(dbSystem);
            roundTrip.GetTagItem("valtuutus.resolves_snap_token").Should().Be(true);
            roundTrip.GetTagItem("valtuutus.straggler").Should().BeNull("the answer came from this round trip");
            ((string[])roundTrip.GetTagItem("valtuutus.queries")!).Should().NotBeEmpty();
        }

        [Fact]
        public async Task Write_span_covers_the_commit_and_reports_its_token()
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options);
            await using var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();

            using var spans = new SpanCollector();
            var written = await writer.Write([new RelationTuple("doc", "d1", "owner", "user", "alice")], [], default);

            var write = spans.Named("valtuutus.write").Should().ContainSingle().Subject;
            write.GetTagItem("valtuutus.snap_token").Should().Be(written.Value);
            write.GetTagItem("db.system.name").Should().Be(dbSystem);
        }
    }
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class TracingSpecs(MySqlFixture fixture) : TracingSpecsBase(fixture, "mysql");
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class TracingSpecs(MariaDbFixture fixture) : TracingSpecsBase(fixture, "mariadb");
}
