using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class TracingSpecs : IAsyncLifetime
{
    private const string Schema = """
        entity user {}
        entity doc {
            relation owner @user;
            relation editor @user;
            permission view := owner or editor;
        }
        """;

    public TracingSpecs(PostgresFixture fixture)
    {
        Fixture = fixture;
    }

    private PostgresFixture Fixture { get; }

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tokenless_check_resolves_its_token_in_the_batched_round_trip_span()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddPostgres(_ => dbFactory);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var written = await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>()
            .Write([new RelationTuple("doc", "d1", "editor", "user", "alice")], [], default);
        var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

        using var spans = new SpanCollector();
        await engine.Check(new CheckRequest("doc", "d1", "view", "user", "alice"), default);

        var check = spans.Named("valtuutus.check").Should().ContainSingle().Subject;
        check.GetTagItem("valtuutus.snap_token").Should().Be(written.Value);
        check.GetTagItem("valtuutus.round_trips").Should().Be(1);
        var roundTrip = spans.ChildrenOf(check).Should().ContainSingle().Subject;
        roundTrip.OperationName.Should().Be("valtuutus.round_trip");
        roundTrip.GetTagItem("db.system.name").Should().Be("postgresql");
        roundTrip.GetTagItem("valtuutus.resolves_snap_token").Should().Be(true);
        ((string[])roundTrip.GetTagItem("valtuutus.queries")!).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Write_span_covers_the_commit_and_reports_its_token()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddPostgres(_ => dbFactory);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();

        using var spans = new SpanCollector();
        var written = await writer.Write([new RelationTuple("doc", "d1", "owner", "user", "alice")], [], default);

        var write = spans.Named("valtuutus.write").Should().ContainSingle().Subject;
        write.GetTagItem("valtuutus.snap_token").Should().Be(written.Value);
        write.GetTagItem("db.system.name").Should().Be("postgresql");
    }
}
