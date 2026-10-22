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

        // The reader runs one statement per round trip, so a tokenless check resolves its token in a
        // round trip of its own before the one that answers it.
        [Fact]
        public async Task Tokenless_check_round_trips_carry_the_db_system()
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
            check.GetTagItem("valtuutus.round_trips").Should().Be(2);
            var roundTrips = await spans.WaitForChildren(check, 2);
            roundTrips.Should().HaveCount(2);
            roundTrips.Should().OnlyContain(a => a.OperationName == "valtuutus.round_trip"
                && (string?)a.GetTagItem("db.system.name") == dbSystem);
            roundTrips.SelectMany(a => (string[])a.GetTagItem("valtuutus.queries")!)
                .Should().Contain("GetLatestSnapToken");
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
