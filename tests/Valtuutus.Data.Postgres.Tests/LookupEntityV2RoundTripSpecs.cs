using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data.Db;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

/// <summary>
/// LookupEntity V2 runs each step's queries in one round trip, and a lookup without a caller token
/// resolves the latest token inside its first round trip; every later step of that request still
/// reads that one token.
/// </summary>
[Collection("PostgreSqlSpec")]
public sealed class LookupEntityV2RoundTripSpecs : IAsyncLifetime
{
    // edit: two sibling direct relations, one query. review: a direct relation plus a terminal
    // userset, two queries in one step. view: a non-terminal userset, one step per group hop.
    // audit: an attribute and a scoped negation, scope entities and both reads in the first step.
    // work: a constrained intersection, candidates first, then their attributes.
    private const string Schema = """
        entity user {}
        entity organization {
            relation member @user;
        }
        entity team {
            relation member @user;
        }
        entity group {
            relation member @user @group#member;
        }
        entity project {
            relation org @organization;
            relation owner @user;
            relation editor @user;
            relation banned @user;
            relation reviewer @team#member;
            relation viewer @group#member;
            attribute active bool;
            attribute status int;
            permission edit := owner or editor;
            permission review := owner or reviewer;
            permission view := viewer;
            permission audit := active or not(banned);
            permission work := isActive(status) and (owner or editor);
        }
        fn isActive(status int) => status == 1;
        """;

    public LookupEntityV2RoundTripSpecs(PostgresFixture fixture)
    {
        Fixture = fixture;
    }

    private PostgresFixture Fixture { get; }

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tokenless_one_step_lookup_resolves_the_token_in_its_only_round_trip()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory, [new RelationTuple("project", "p1", "owner", "user", "alice")]);

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

        var page = await engine.LookupEntity(new LookupEntityRequest("project", "edit", "user", "alice"), default);

        page.EntityIds.Should().Equal("p1");
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_step_with_several_queries_is_one_round_trip()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory, [new RelationTuple("project", "p1", "owner", "user", "alice")]);
        await Write(dbFactory, [new RelationTuple("project", "p2", "reviewer", "team", "t1", "member")]);
        var token = await Write(dbFactory, [new RelationTuple("team", "t1", "member", "user", "alice")]);

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

        var page = await engine.LookupEntity(
            new LookupEntityRequest("project", "review", "user", "alice") { SnapToken = token }, default);

        page.EntityIds.Should().Equal("p1", "p2");
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task Scoped_attribute_and_negation_read_scope_and_data_in_the_first_round_trip()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory,
            [
                new RelationTuple("project", "p1", "org", "organization", "o1"),
                new RelationTuple("project", "p2", "org", "organization", "o1"),
                new RelationTuple("project", "p3", "org", "organization", "o2"),
                new RelationTuple("project", "p2", "banned", "user", "alice"),
            ],
            [new AttributeTuple("project", "p2", "active", JsonValue.Create(true)!)]);

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

        var page = await engine.LookupEntity(
            new LookupEntityRequest("project", "audit", "user", "alice") { Scope = new EntityScope("org", "organization", "o1") },
            default);

        // Step 1: token, attribute, scope entities, banned; step 2: the complement of banned.
        page.EntityIds.Should().Equal("p1", "p2");
        counter.Count.Should().Be(2);
    }

    [Fact]
    public async Task Constrained_intersection_takes_one_round_trip_per_step()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory,
            [
                new RelationTuple("project", "p1", "owner", "user", "alice"),
                new RelationTuple("project", "p2", "editor", "user", "alice"),
            ],
            [
                new AttributeTuple("project", "p1", "status", JsonValue.Create(1)!),
                new AttributeTuple("project", "p2", "status", JsonValue.Create(0)!),
            ]);

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

        var page = await engine.LookupEntity(new LookupEntityRequest("project", "work", "user", "alice"), default);

        page.EntityIds.Should().Equal("p1");
        counter.Count.Should().Be(2);
    }

    [Fact]
    public async Task Write_committed_between_steps_is_invisible_to_the_running_lookup()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory, [new RelationTuple("project", "p1", "viewer", "group", "g1", "member")]);
        await Write(dbFactory, [new RelationTuple("group", "g1", "member", "group", "g2", "member")]);
        await Write(dbFactory, [new RelationTuple("group", "g2", "member", "user", "bob")]);

        var counter = new RoundTripCounter();
        var wroteMidRequest = false;
        await using var sp = BuildServices(dbFactory, counter, beforeRoundTrip: async roundTrip =>
        {
            if (roundTrip != 2) return;
            // Step 1 already pinned the token; this grant commits after it.
            await Write(dbFactory, [new RelationTuple("project", "p2", "viewer", "group", "g2", "member")]);
            wroteMidRequest = true;
        });
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

        var duringWrite = await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "bob"), default);
        var afterWrite = await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "bob"), default);

        wroteMidRequest.Should().BeTrue("the lookup must have needed a second step for this spec to mean anything");
        duringWrite.EntityIds.Should().Equal(["p1"], "the grant committed after the request's token was resolved");
        afterWrite.EntityIds.Should().Equal(["p1", "p2"], "a new request resolves a new token that includes the grant");
    }

    [Fact]
    public async Task Explicit_token_lookups_keep_binding_that_token()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory, [new RelationTuple("project", "p2", "reviewer", "team", "t1", "member")]);
        var before = await Write(dbFactory, [new RelationTuple("project", "p1", "owner", "user", "bob")]);
        await Write(dbFactory, [new RelationTuple("team", "t1", "member", "user", "bob")]);

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

        // A tokenless lookup first leaves its resolved token on the pooled connection; the
        // explicit-token request, two queries batched with the token as a parameter, must still
        // read only its own snapshot.
        (await engine.LookupEntity(new LookupEntityRequest("project", "review", "user", "bob"), default))
            .EntityIds.Should().Equal("p1", "p2");
        (await engine.LookupEntity(new LookupEntityRequest("project", "review", "user", "bob") { SnapToken = before }, default))
            .EntityIds.Should().Equal("p1");
    }

    // k=1: the direct read, then one hop per step: g2, g1, g0, then nothing. From k=2 the first
    // query reads the direct grant as level 0. k=2: g3, g2, g1, then g0 and an empty level. k=3:
    // g3 to g0, then nothing new. k=4: g3 to g0 and an empty last level.
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    public async Task Nested_group_hops_share_round_trips(int hops, int roundTrips)
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        await Write(dbFactory,
        [
            new RelationTuple("group", "g3", "member", "user", "u"),
            new RelationTuple("group", "g2", "member", "group", "g3", "member"),
            new RelationTuple("group", "g1", "member", "group", "g2", "member"),
            new RelationTuple("group", "g0", "member", "group", "g1", "member"),
        ]);

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter, hops: hops);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

        var page = await engine.LookupEntity(new LookupEntityRequest("group", "member", "user", "u"), default);

        page.EntityIds.Should().Equal("g0", "g1", "g2", "g3");
        counter.Count.Should().Be(roundTrips);
    }

    [Fact]
    public async Task Nested_group_with_an_explicit_token_reads_its_direct_grant_in_the_first_levels_query()
    {
        var dbFactory = ((IWithDbConnectionFactory)Fixture).DbFactory;
        var token = await Write(dbFactory,
        [
            new RelationTuple("group", "g3", "member", "user", "u"),
            new RelationTuple("group", "g2", "member", "group", "g3", "member"),
            new RelationTuple("group", "g1", "member", "group", "g2", "member"),
            new RelationTuple("group", "g0", "member", "group", "g1", "member"),
        ]);
        await Write(dbFactory, [new RelationTuple("group", "g9", "member", "user", "u")]);

        var counter = new RoundTripCounter();
        await using var sp = BuildServices(dbFactory, counter, hops: 2);
        await using var scope = sp.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();

        var page = await engine.LookupEntity(
            new LookupEntityRequest("group", "member", "user", "u") { SnapToken = token }, default);

        page.EntityIds.Should().Equal("g0", "g1", "g2", "g3");
        counter.Count.Should().Be(2);
    }

    private static ServiceProvider BuildServices(DbConnectionFactory dbFactory, RoundTripCounter counter,
        Func<int, Task>? beforeRoundTrip = null, int? hops = null)
    {
        counter.BeforeRoundTrip = beforeRoundTrip;
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        var options = new ValtuutusPostgresOptions("public", "transactions", "relation_tuples", "attributes");
        services.AddPostgres(_ => dbFactory, hops is { } k ? options with { LookupHopsPerQuery = k } : options)
            .AddConcurrentQueryLimit(3);
        services.AddSingleton(counter);
        services.Replace(ServiceDescriptor.Scoped<IDataReaderProvider>(sp =>
            new CountingReaderProvider(ActivatorUtilities.CreateInstance<PostgresDataReaderProvider>(sp), counter)));
        services.Replace(ServiceDescriptor.Singleton<IRelationalBatchOps>(sp =>
            new CountingBatchOps(
                new PostgresBatchOps(sp.GetRequiredService<DbConnectionFactory>(),
                    sp.GetRequiredService<ValtuutusDataOptions>(),
                    sp.GetRequiredService<ValtuutusPostgresOptions>()),
                counter)));
        return services.BuildServiceProvider();
    }

    private static async Task<SnapToken> Write(DbConnectionFactory dbFactory, RelationTuple[] tuples,
        AttributeTuple[]? attributes = null)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddPostgres(_ => dbFactory);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write(tuples, attributes ?? [], default);
    }
}
