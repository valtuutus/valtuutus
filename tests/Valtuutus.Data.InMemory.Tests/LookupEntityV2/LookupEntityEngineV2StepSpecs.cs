using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupEntity.V2;

namespace Valtuutus.Data.InMemory.Tests.LookupEntityV2;

public class LookupEntityEngineV2StepSpecs
{
    private const string Schema = """
        entity user {}
        entity service {}
        entity organization {
            relation admin @user;
        }
        entity team {
            relation member @user;
        }
        entity group {
            relation member @user @group#member;
            relation org @organization;
        }
        entity folder {
            relation org @organization;
            relation parent @folder;
            relation owner @user;
            permission view := owner or parent.view;
        }
        entity project {
            relation org @organization;
            relation owner @user;
            relation editor @user;
            relation banned @user;
            relation bot @service;
            relation reviewer @team#member;
            relation viewer @group#member;
            attribute active bool;
            attribute status int;
            permission edit := owner or editor;
            permission automate := bot or owner;
            permission review := reviewer;
            permission view := viewer;
            permission manage := org.admin;
            permission work := isActive(status) and (owner or editor);
            permission keep := owner and not(banned);
            permission audit := active or not(banned);
            permission open := isActive(status) or owner;
            permission reopen := open or isActive(status);
        }
        fn isActive(status int) => status == 1;
        """;

    private sealed class RecordingRunner(ILookupEntityQueryRunner inner) : ILookupEntityQueryRunner
    {
        public List<(LookupEntityQuery[] Queries, SnapToken? SnapToken)> Steps { get; } = [];

        public ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupEntityQuery> queries, SnapToken? snapToken,
            CancellationToken cancellationToken)
        {
            // Ids are readable only while the step runs, so each recorded query keeps a copy.
            Steps.Add((queries.Select(q => q.For(q.Ids.ToArray(), q.Scope)).ToArray(), snapToken));
            return inner.RunAsync(queries, snapToken, cancellationToken);
        }

        public IEnumerable<LookupEntityQuery> All => Steps.SelectMany(s => s.Queries);
    }

    private static async Task<(ILookupEntityEngine Engine, RecordingRunner Runner, SnapToken Written)> Setup(
        RelationTuple[] tuples, AttributeTuple[]? attributes = null)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddInMemory();
        services.AddValtuutusLookupEntityV2();
        RecordingRunner? runner = null;
        services.Replace(ServiceDescriptor.Scoped<ILookupEntityQueryRunner>(sp =>
            runner = new RecordingRunner(new DefaultLookupEntityQueryRunner(sp.GetRequiredService<IDataReaderProvider>()))));
        var provider = services.BuildServiceProvider();
        var written = await provider.GetRequiredService<IDataWriterProvider>().Write(tuples, attributes ?? [], default);
        var engine = provider.GetRequiredService<ILookupEntityEngine>();
        return (engine, runner!, written);
    }

    private static readonly RelationTuple[] NestedGroupTuples =
    [
        new("project", "p1", "viewer", "group", "g1", "member"),
        new("group", "g1", "member", "group", "g2", "member"),
        new("group", "g2", "member", "user", "u1"),
    ];

    [Fact]
    public async Task Sibling_direct_relations_share_one_query()
    {
        var (engine, runner, _) = await Setup([
            new("project", "p1", "owner", "user", "u1"),
            new("project", "p2", "editor", "user", "u1"),
        ]);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "edit", "user", "u1"), default);

        result.EntityIds.Should().Equal("p1", "p2");
        var query = runner.Steps.Should().ContainSingle().Which.Queries.Should().ContainSingle().Subject;
        query.Kind.Should().Be(LookupEntityQueryKind.RelationsMulti);
        query.Names.Should().BeEquivalentTo("owner", "editor");
        query.Ids.Should().Equal("u1");
        query.SubjectRelation.Should().BeEmpty();
    }

    [Fact]
    public async Task Unreachable_branch_asks_nothing()
    {
        var (engine, runner, _) = await Setup([new("project", "p1", "owner", "user", "u1")]);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "automate", "user", "u1"), default);

        result.EntityIds.Should().Equal("p1");
        var query = runner.Steps.Should().ContainSingle().Which.Queries.Should().ContainSingle().Subject;
        query.Kind.Should().Be(LookupEntityQueryKind.Relations);
        query.Relation.Should().Be("owner");
    }

    [Fact]
    public async Task Terminal_userset_is_one_joined_query()
    {
        var (engine, runner, _) = await Setup([
            new("project", "p1", "reviewer", "team", "t1", "member"),
            new("team", "t1", "member", "user", "u1"),
        ]);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "review", "user", "u1"), default);

        result.EntityIds.Should().Equal("p1");
        var query = runner.Steps.Should().ContainSingle().Which.Queries.Should().ContainSingle().Subject;
        query.Kind.Should().Be(LookupEntityQueryKind.Joined);
        query.Relation.Should().Be("reviewer");
        query.SubEntityType.Should().Be("team");
        query.SubjectRelation.Should().Be("member");
        query.SubRelation.Should().Be("member");
        query.SubjectType.Should().Be("user");
        query.Ids.Should().Equal("u1");
    }

    [Fact]
    public async Task Tuple_to_userset_with_terminal_target_is_one_joined_query()
    {
        var (engine, runner, _) = await Setup([
            new("project", "p1", "org", "organization", "o1"),
            new("organization", "o1", "admin", "user", "u1"),
        ]);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "manage", "user", "u1"), default);

        result.EntityIds.Should().Equal("p1");
        var query = runner.Steps.Should().ContainSingle().Which.Queries.Should().ContainSingle().Subject;
        query.Kind.Should().Be(LookupEntityQueryKind.Joined);
        query.Relation.Should().Be("org");
        query.SubEntityType.Should().Be("organization");
        query.SubjectRelation.Should().BeEmpty();
        query.SubRelation.Should().Be("admin");
    }

    [Fact]
    public async Task Joined_hop_at_spent_depth_asks_nothing()
    {
        var (engine, runner, _) = await Setup([
            new("project", "p1", "org", "organization", "o1"),
            new("organization", "o1", "admin", "user", "u1"),
        ]);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "manage", "user", "u1", depth: 1), default);

        result.EntityIds.Should().BeEmpty();
        runner.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task Subject_bound_reads_repeated_by_recursion_run_once()
    {
        var (engine, runner, _) = await Setup(NestedGroupTuples);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "u1"), default);

        result.EntityIds.Should().Equal("p1");
        var first = runner.Steps[0].Queries.Should().ContainSingle().Subject;
        first.Kind.Should().Be(LookupEntityQueryKind.Relations);
        first.EntityType.Should().Be("group");
        first.Relation.Should().Be("member");
        first.Ids.Should().Equal("u1");
        runner.All.Count(q => q.Ids.SequenceEqual(["u1"])).Should().Be(1);
    }

    [Fact]
    public async Task Recursive_userset_stops_at_the_first_hop_that_finds_nothing_new()
    {
        var (engine, runner, _) = await Setup(NestedGroupTuples);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "u1"), default);

        // The subject's own groups, a hop finding g1, a hop finding nothing, then the project read.
        result.EntityIds.Should().Equal("p1");
        runner.Steps.Should().HaveCount(4);
    }

    [Fact]
    public async Task Scoped_recursive_userset_scopes_the_root_level_over_an_unscoped_walk()
    {
        var (engine, runner, _) = await Setup(NestedGroupTuples.Concat([
            new RelationTuple("group", "g1", "org", "organization", "o1"),
        ]).ToArray());

        var result = await engine.LookupEntity(
            new LookupEntityRequest("group", "member", "user", "u1") { Scope = new EntityScope("org", "organization", "o1") },
            default);

        // g1 is the only group in the scope. Steps: the direct reads (scoped and unscoped), the walk's
        // hop to g1, the walk's hop finding nothing, then the scoped root hop over the walk's groups.
        result.EntityIds.Should().Equal("g1");
        runner.Steps.Should().HaveCount(4);
        runner.Steps[3].Queries.Should().ContainSingle().Which.Scope
            .Should().Be(new EntityScope("org", "organization", "o1"));
    }

    [Fact]
    public async Task Scope_applies_to_the_root_frame_only()
    {
        var (engine, runner, _) = await Setup([
            new("folder", "f1", "org", "organization", "o1"),
            new("folder", "f2", "org", "organization", "o1"),
            new("folder", "f3", "org", "organization", "o2"),
            new("folder", "f1", "owner", "user", "u1"),
            new("folder", "f2", "parent", "folder", "f1"),
            new("folder", "f3", "parent", "folder", "f1"),
        ]);

        var result = await engine.LookupEntity(
            new LookupEntityRequest("folder", "view", "user", "u1") { Scope = new EntityScope("org", "organization", "o1") },
            default);

        result.EntityIds.Should().Equal("f1", "f2");
        var owners = runner.Steps[0].Queries.Where(q => q.Relation == "owner").ToList();
        owners.Should().HaveCount(2);
        owners.Should().ContainSingle(q => q.Scope == new EntityScope("org", "organization", "o1"));
        owners.Should().ContainSingle(q => q.Scope == null);
    }

    [Fact]
    public async Task Scope_entities_are_read_once_per_request()
    {
        var (engine, runner, _) = await Setup(
            [
                new("project", "p1", "org", "organization", "o1"),
                new("project", "p2", "org", "organization", "o1"),
                new("project", "p2", "banned", "user", "u1"),
            ],
            [new AttributeTuple("project", "p2", "active", JsonValue.Create(true)!)]);

        var result = await engine.LookupEntity(
            new LookupEntityRequest("project", "audit", "user", "u1") { Scope = new EntityScope("org", "organization", "o1") },
            default);

        result.EntityIds.Should().Equal("p1", "p2");
        runner.All.Count(q => q.Relation == "org").Should().Be(1);
    }

    [Fact]
    public async Task Identical_attribute_reads_run_once()
    {
        var (engine, runner, _) = await Setup(
            [new("project", "p2", "owner", "user", "u1")],
            [new AttributeTuple("project", "p1", "status", JsonValue.Create(1)!)]);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "reopen", "user", "u1"), default);

        result.EntityIds.Should().Equal("p1", "p2");
        runner.All.Count(q => q.Kind == LookupEntityQueryKind.Attributes).Should().Be(1);
    }

    [Fact]
    public async Task Constrained_intersection_reads_attributes_of_its_candidates_only()
    {
        var (engine, runner, _) = await Setup(
            [
                new("project", "p1", "owner", "user", "u1"),
                new("project", "p2", "editor", "user", "u1"),
            ],
            [
                new AttributeTuple("project", "p1", "status", JsonValue.Create(1)!),
                new AttributeTuple("project", "p2", "status", JsonValue.Create(0)!),
                new AttributeTuple("project", "p3", "status", JsonValue.Create(1)!),
            ]);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "work", "user", "u1"), default);

        result.EntityIds.Should().Equal("p1");
        runner.Steps.Should().HaveCount(2);
        var check = runner.Steps[1].Queries.Should().ContainSingle().Subject;
        check.Kind.Should().Be(LookupEntityQueryKind.AttributesWithIds);
        check.Ids.Should().BeEquivalentTo("p1", "p2");
    }

    [Fact]
    public async Task Intersection_with_negate_subtracts_without_listing_every_entity()
    {
        var (engine, runner, _) = await Setup([
            new("project", "p1", "owner", "user", "u1"),
            new("project", "p2", "owner", "user", "u1"),
            new("project", "p2", "banned", "user", "u1"),
        ]);

        var result = await engine.LookupEntity(new LookupEntityRequest("project", "keep", "user", "u1"), default);

        result.EntityIds.Should().Equal("p1");
        runner.All.Should().NotContain(q => q.Kind == LookupEntityQueryKind.EntitiesExcluding);
    }

    [Fact]
    public async Task Tokenless_lookup_resolves_the_snap_token_once_and_pins_it()
    {
        var (engine, runner, written) = await Setup(NestedGroupTuples);

        await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "u1"), default);

        runner.Steps.Should().HaveCountGreaterThan(1);
        runner.Steps[0].SnapToken.Should().BeNull();
        runner.Steps.Skip(1).Should().OnlyContain(s => s.SnapToken == written);
    }

    [Fact]
    public async Task Explicit_snap_token_reaches_every_step()
    {
        var (engine, runner, written) = await Setup(NestedGroupTuples);

        await engine.LookupEntity(new LookupEntityRequest("project", "view", "user", "u1") { SnapToken = written }, default);

        runner.Steps.Should().OnlyContain(s => s.SnapToken == written);
    }

    [Fact]
    public async Task Concurrent_lookups_return_the_same_pages()
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddInMemory();
        services.AddValtuutusLookupEntityV2();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDataWriterProvider>().Write(
            [
                new("project", "p1", "viewer", "group", "g1", "member"),
                new("group", "g1", "member", "group", "g2", "member"),
                new("group", "g2", "member", "user", "u1"),
                new("project", "p2", "owner", "user", "u1"),
                new("project", "p3", "editor", "user", "u1"),
                new("project", "p3", "banned", "user", "u1"),
                new("project", "p4", "org", "organization", "o1"),
                new("organization", "o1", "admin", "user", "u1"),
            ],
            [
                new AttributeTuple("project", "p2", "status", JsonValue.Create(1)!),
                new AttributeTuple("project", "p3", "status", JsonValue.Create(1)!),
            ], default);
        // Fixed pages, so a pooling fault that repeats on every run still fails. The scoped and
        // unknown-subject requests cover scope reads and a dependent hop that finds nothing.
        (string Name, LookupEntityRequest Request, string[] Expected)[] cases =
        [
            ("view", new LookupEntityRequest("project", "view", "user", "u1"), ["p1"]),
            ("edit", new LookupEntityRequest("project", "edit", "user", "u1"), ["p2", "p3"]),
            ("work", new LookupEntityRequest("project", "work", "user", "u1"), ["p2", "p3"]),
            ("keep", new LookupEntityRequest("project", "keep", "user", "u1"), ["p2"]),
            ("manage", new LookupEntityRequest("project", "manage", "user", "u1"), ["p4"]),
            ("reopen", new LookupEntityRequest("project", "reopen", "user", "u1"), ["p2", "p3"]),
            ("audit", new LookupEntityRequest("project", "audit", "user", "u1"), ["p1", "p2", "p4"]),
            ("audit scoped", new LookupEntityRequest("project", "audit", "user", "u1")
                { Scope = new EntityScope("org", "organization", "o1") }, ["p4"]),
            ("view unknown subject", new LookupEntityRequest("project", "view", "user", "u2"), []),
        ];

        // One engine over the provider that holds the written data; the engine and its default
        // runner keep no per-request state, so concurrent lookups share it.
        var engine = provider.GetRequiredService<ILookupEntityEngine>();
        async Task<IReadOnlyList<string>> Lookup(LookupEntityRequest request) =>
            (await engine.LookupEntity(request, default)).EntityIds;

        var runs = Enumerable.Range(0, 450).Select(i => Task.Run(async () =>
        {
            var (name, request, expected) = cases[i % cases.Length];
            return (name, expected, Ids: await Lookup(request));
        }));

        foreach (var (name, expected, ids) in await Task.WhenAll(runs))
            ids.Should().Equal(expected, name);
    }
}
