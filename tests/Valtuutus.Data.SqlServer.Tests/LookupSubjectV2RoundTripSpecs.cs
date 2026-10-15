using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.SqlServer.Tests;

/// <summary>
/// LookupSubject V2 runs each step's queries in one round trip, and a lookup without a caller
/// token resolves the latest token inside its first round trip; every later step of that request
/// still reads that one token.
/// </summary>
[Collection("SqlServerSpec")]
public sealed class LookupSubjectV2RoundTripSpecs : IAsyncLifetime
{
    // edit: two sibling direct relations, one query. review: a direct relation plus a terminal
    // userset, two queries in one step. view: a non-terminal userset, one step per group hop.
    private const string Schema = """
        entity user {}
        entity team {
            relation member @user;
        }
        entity group {
            relation member @user @group#member;
        }
        entity project {
            relation owner @user;
            relation editor @user;
            relation reviewer @team#member;
            relation viewer @group#member;
            permission edit := owner or editor;
            permission review := owner or reviewer;
            permission view := viewer;
        }
        """;

    public LookupSubjectV2RoundTripSpecs(SqlServerFixture fixture)
    {
        Fixture = fixture;
    }

    private SqlServerFixture Fixture { get; }

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tokenless_one_step_lookup_resolves_the_token_in_its_only_round_trip()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory, new RelationTuple("project", "p1", "owner", "user", "alice"));

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

        var subjects = await engine.Lookup(new LookupSubjectRequest("project", "edit", "user", "p1"), default);

        subjects.Should().BeEquivalentTo("alice");
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_step_with_several_queries_is_one_round_trip()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory, new RelationTuple("project", "p1", "owner", "user", "alice"));
        await Write(dbFactory, new RelationTuple("project", "p1", "reviewer", "team", "t1", "member"));
        var token = await Write(dbFactory, new RelationTuple("team", "t1", "member", "user", "bob"));

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

        var subjects = await engine.Lookup(
            new LookupSubjectRequest("project", "review", "user", "p1") { SnapToken = token }, default);

        subjects.Should().BeEquivalentTo("alice", "bob");
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task Write_committed_between_steps_is_invisible_to_the_running_lookup()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory, new RelationTuple("project", "p1", "viewer", "group", "g1", "member"));
        await Write(dbFactory, new RelationTuple("group", "g1", "member", "group", "g2", "member"));
        await Write(dbFactory, new RelationTuple("group", "g2", "member", "user", "bob"));

        var counter = new RoundTripCounter();
        var wroteMidRequest = false;
        await using var sp = BuildServices(dbFactory, counter, beforeRoundTrip: async roundTrip =>
        {
            if (roundTrip != 2) return;
            // Step 1 already pinned the token; this grant commits after it, before step 2 reads g1.
            await Write(dbFactory, new RelationTuple("group", "g1", "member", "user", "alice"));
            wroteMidRequest = true;
        }, hops: 1);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

        var duringWrite = await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1"), default);
        var afterWrite = await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1"), default);

        wroteMidRequest.Should().BeTrue("the lookup must have needed a second step for this spec to mean anything");
        duringWrite.Should().BeEquivalentTo(["bob"], "the grant committed after the request's token was resolved");
        afterWrite.Should().BeEquivalentTo(["alice", "bob"], "a new request resolves a new token that includes the grant");
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 2)]
    [InlineData(4, 1)]
    public async Task Nested_userset_levels_share_round_trips(int hops, int roundTrips)
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory, new RelationTuple("group", "g0", "member", "user", "u0"));
        await Write(dbFactory, new RelationTuple("group", "g0", "member", "group", "g1", "member"));
        await Write(dbFactory, new RelationTuple("group", "g1", "member", "user", "u1"));
        await Write(dbFactory, new RelationTuple("group", "g1", "member", "group", "g2", "member"));
        await Write(dbFactory, new RelationTuple("group", "g2", "member", "user", "u2"));
        await Write(dbFactory, new RelationTuple("group", "g2", "member", "group", "g3", "member"));
        await Write(dbFactory, new RelationTuple("group", "g3", "member", "user", "u3"));

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter, hops: hops);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

        var subjects = await engine.Lookup(new LookupSubjectRequest("group", "member", "user", "g0"), default);

        subjects.Should().BeEquivalentTo("u0", "u1", "u2", "u3");
        counter.Count.Should().Be(roundTrips);
    }

    [Fact]
    public async Task Explicit_token_lookups_keep_binding_that_token()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        var before = await Write(dbFactory, new RelationTuple("project", "p1", "owner", "user", "bob"));
        await Write(dbFactory, new RelationTuple("project", "p1", "editor", "user", "alice"));

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

        // A tokenless lookup first leaves its resolved token on the pooled connection; the
        // explicit-token request must still read only its own snapshot.
        (await engine.Lookup(new LookupSubjectRequest("project", "edit", "user", "p1"), default))
            .Should().BeEquivalentTo("alice", "bob");
        (await engine.Lookup(new LookupSubjectRequest("project", "edit", "user", "p1") { SnapToken = before }, default))
            .Should().BeEquivalentTo("bob");
    }

    private static ServiceProvider BuildServices(DbConnectionFactory dbFactory, RoundTripCounter counter,
        Func<int, Task>? beforeRoundTrip = null, int? hops = null)
    {
        counter.BeforeRoundTrip = beforeRoundTrip;
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        var options = new ValtuutusSqlServerOptions("dbo", "transactions", "relation_tuples", "attributes");
        services.AddSqlServer(_ => dbFactory, hops is { } k ? options with { LookupHopsPerQuery = k } : options)
            .AddConcurrentQueryLimit(3);
        services.AddValtuutusLookupSubjectV2();
        services.AddSingleton(counter);
        services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
            new CountingReaderProvider(ActivatorUtilities.CreateInstance<SqlServerDataReaderProvider>(sp), counter)));
        services.Replace(ServiceDescriptor.Singleton<IRelationalBatchOps>(sp =>
            new CountingBatchOps(ActivatorUtilities.CreateInstance<SqlServerBatchOps>(sp), counter)));
        return services.BuildServiceProvider();
    }

    private static async Task<SnapToken> Write(DbConnectionFactory dbFactory, RelationTuple tuple)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddSqlServer(_ => dbFactory);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write([tuple], [], default);
    }
}
