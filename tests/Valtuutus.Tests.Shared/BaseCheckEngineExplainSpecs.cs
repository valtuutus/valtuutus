using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data;

namespace Valtuutus.Tests.Shared;

public abstract class BaseCheckEngineExplainSpecs : IAsyncLifetime
{
    protected BaseCheckEngineExplainSpecs(IDatabaseFixture fixture) { Fixture = fixture; }
    protected IDatabaseFixture Fixture { get; }
    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);
    protected virtual bool UseCheckV2 => false;

    protected async ValueTask<ICheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        string? schema = null)
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(schema ?? TestsConsts.DefaultSchema);
        AddSpecificProvider(services).AddConcurrentQueryLimit(3);
        if (UseCheckV2)
            services.AddValtuutusCheckV2();
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
        if (tuples.Length == 0 && attributes.Length == 0) return engine;
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await writer.Write(tuples, attributes, default);
        return engine;
    }

    public async Task InitializeAsync() => await Fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Explain_DirectRelation_ReturnsTrueWithRelationNode()
    {
        var engine = await CreateEngine(
            [new RelationTuple("workspace", "expl-1", "owner", "user", "alice")],
            []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-1",
            Permission = "delete",     // delete := owner
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        result.Root.Type.Should().Be(CheckNodeType.Permission);
        result.Root.Name.Should().Be("delete");
        result.Root.Result.Should().BeTrue();
        result.Root.Children.Should().HaveCount(1);
        var ownerNode = result.Root.Children[0];
        ownerNode.Type.Should().Be(CheckNodeType.Relation);
        ownerNode.Name.Should().Be("owner");
        ownerNode.Detail.Should().Be("direct tuple");
        ownerNode.Result.Should().BeTrue();
    }

    [Fact]
    public async Task Explain_DirectRelation_ReturnsFalseWithNoTupleDetail()
    {
        var engine = await CreateEngine([], []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-2",
            Permission = "delete",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeFalse();
        result.Root.Children.Should().HaveCount(1);
        var ownerNode = result.Root.Children[0];
        ownerNode.Type.Should().Be(CheckNodeType.Relation);
        ownerNode.Name.Should().Be("owner");
        ownerNode.Detail.Should().Be("no matching tuple");
        ownerNode.Result.Should().BeFalse();
    }

    [Fact]
    public async Task Explain_FunctionCall_ReturnsFunctionNodeWithDetail()
    {
        // project.edit := (parent.admin or team.member) and isActiveStatus(status)
        // Seed: alice is parent workspace admin AND status=1
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "expl-6", "parent", "workspace", "expl-ws6"),
                new RelationTuple("workspace", "expl-ws6", "admin", "user", "alice")
            ],
            [new AttributeTuple("project", "expl-6", "status", System.Text.Json.Nodes.JsonValue.Create(1))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "expl-6",
            Permission = "edit",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        var fnNode = FindNodeByType(result.Root, CheckNodeType.Function);
        fnNode.Should().NotBeNull();
        fnNode!.Name.Should().Be("isActiveStatus");
        fnNode.Detail.Should().Be("fn result=True");
        fnNode.Result.Should().BeTrue();
    }

    [Fact]
    public async Task Explain_TupleToUserSet_ResolvesViaRelatedEntity()
    {
        // project.edit := (parent.admin or ...) and isActiveStatus(status)
        // alice is admin of workspace ws1; project parent is ws1; status=1
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "expl-7", "parent", "workspace", "expl-ws7"),
                new RelationTuple("workspace", "expl-ws7", "admin", "user", "alice")
            ],
            [new AttributeTuple("project", "expl-7", "status", System.Text.Json.Nodes.JsonValue.Create(1))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "expl-7",
            Permission = "edit",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        // edit's tree has TWO TupleToUserSet nodes (parent.admin and team.member — no team tuple
        // seeded here, so team.member is false); a union's children attach in completion order,
        // not declaration order (V1 parity: CheckEngine also attaches per-child on completion),
        // so an unqualified FindNodeByType could return either one. Match by name, same
        // convention as Explain_TupleToUserSet_SlowPathSingleRelation_RecordsNode/
        // Explain_TupleToUserSet_MultipleRelations_ParallelPath_RecordsNodes below.
        var ttuNode = FindNode(result.Root, n => n.Type == CheckNodeType.TupleToUserSet && n.Name == "parent.admin");
        ttuNode.Should().NotBeNull();
        ttuNode!.Result.Should().BeTrue();
    }

    [Fact]
    public async Task Explain_NoInfiniteRecursion_ReturnsFiniteTree()
    {
        var engine = await CreateEngine(
            [new RelationTuple("project", "expl-8", "member", "user", "alice")],
            []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "expl-8",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        result.Root.Name.Should().Be("view");
    }

    [Fact]
    public async Task Explain_NegateExpression_ReturnsNegatedResult()
    {
        // Custom schema: permission banned := not(member)
        // alice is NOT a member → banned = true
        const string negateSchema = """
            entity user {}
            entity doc {
                relation member @user;
                permission banned := not(member);
            }
            """;
        var engine = await CreateEngine([], [], negateSchema);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "doc", EntityId = "expl-n1",
            Permission = "banned",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        result.Root.Children.Should().HaveCount(1);
        var memberNode = result.Root.Children[0];
        memberNode.Name.Should().Be("member");
        memberNode.Result.Should().BeFalse();
    }

    [Fact]
    public async Task Explain_MemoizedSubCheck_RecordsMemoizedDetail()
    {
        // edit := owner and view; view := owner
        // The "owner" relation is evaluated twice in one Explain call —
        // the second evaluation should hit the memo cache.
        const string memoSchema = """
            entity user {}
            entity doc {
                relation owner @user;
                permission view := owner;
                permission edit := owner and view;
            }
            """;
        var engine = await CreateEngine(
            [new RelationTuple("doc", "expl-m1", "owner", "user", "alice")],
            [], memoSchema);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "doc", EntityId = "expl-m1",
            Permission = "edit",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        var memoizedNode = FindNode(result.Root, n => n.Detail == "memoized");
        memoizedNode.Should().NotBeNull("a repeated sub-check should be recorded as memoized");
    }

    [Fact]
    public async Task Explain_DepthLimitReached_ReturnsFalseWithDetail()
    {
        var engine = await CreateEngine(
            [new RelationTuple("workspace", "expl-dl1", "owner", "user", "alice")],
            []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-dl1",
            Permission = "delete",
            SubjectType = "user", SubjectId = "alice",
            Depth = 0
        }, CancellationToken.None);

        result.Result.Should().BeFalse();
        result.Root.Detail.Should().Be("depth limit reached");
    }

    [Fact]
    public async Task Explain_IndirectRelationViaSubRelation_ResolvesAndRecordsChildNode()
    {
        // project.member @user @team#member — indirect path via team
        // alice is a direct member of team expl-t9; team is project member via #member
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "expl-9", "member", "team", "expl-t9", "member"),
                new RelationTuple("team", "expl-t9", "member", "user", "alice"),
            ],
            []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "expl-9",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        // The Relation node for "member" resolves via the indirect (sub-relation) path
        var memberRelationNode = FindNode(result.Root, n => n.Type == CheckNodeType.Relation && n.Name == "member");
        memberRelationNode.Should().NotBeNull("member relation node should appear in tree");
        memberRelationNode!.Children.Should().NotBeEmpty("indirect path creates child nodes");
    }

    [Fact]
    public async Task Explain_TupleToUserSet_SlowPathSingleRelation_RecordsNode()
    {
        // project.edit := (parent.admin or team.member) and isActiveStatus(status)
        // team.member has sub-relation paths (@group#member) → fast path skipped
        // One team tuple → slow path single-relation branch
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "expl-10", "team", "team", "expl-t10"),
                new RelationTuple("team", "expl-t10", "member", "user", "alice"),
            ],
            [new AttributeTuple("project", "expl-10", "status", System.Text.Json.Nodes.JsonValue.Create(1))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "expl-10",
            Permission = "edit",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        // parent.admin (fast-path) is also a TTU node but returns false — find team.member specifically
        var ttuNode = FindNode(result.Root, n => n.Type == CheckNodeType.TupleToUserSet && n.Name == "team.member");
        ttuNode.Should().NotBeNull("TTU node for team.member should appear in tree");
        ttuNode!.Result.Should().BeTrue();
        ttuNode.Children.Should().NotBeEmpty("slow-path creates child nodes for the resolved relation");
    }

    [Fact]
    public async Task Explain_SubjectTypeCannotReach_ReturnsFalseWithDetail()
    {
        // "organization" cannot reach workspace.delete (only users can via owner relation)
        var engine = await CreateEngine([], []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-sc1",
            Permission = "delete",
            SubjectType = "organization", SubjectId = "org-1"
        }, CancellationToken.None);

        result.Result.Should().BeFalse();
        result.Root.Detail.Should().Be("subject type cannot reach permission");
    }

    [Fact]
    public async Task Explain_IndirectRelation_MultipleIndirectPaths_RecordsAllChildren()
    {
        // project.member has @user @team#member — two indirect team tuples, alice in one
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "expl-11", "member", "team", "expl-t11a", "member"),
                new RelationTuple("project", "expl-11", "member", "team", "expl-t11b", "member"),
                new RelationTuple("team", "expl-t11a", "member", "user", "alice"),
            ],
            []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "expl-11",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        // The member Relation node resolves via multiple indirect paths
        var memberNode = FindNode(result.Root, n => n.Type == CheckNodeType.Relation && n.Name == "member");
        memberNode.Should().NotBeNull();
        memberNode!.Children.Count.Should().BeGreaterThanOrEqualTo(2, "one child per indirect relation tuple");
    }

    [Fact]
    public async Task Explain_TupleToUserSet_MultipleRelations_ParallelPath_RecordsNodes()
    {
        // project.edit with two team tuples — team.member has sub-relation paths so batch
        // fast-path is skipped; falls through to parallel multi-relation path in CheckTupleToUserSet
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "expl-12", "team", "team", "expl-t12a"),
                new RelationTuple("project", "expl-12", "team", "team", "expl-t12b"),
                new RelationTuple("team", "expl-t12a", "member", "user", "alice"),
            ],
            [new AttributeTuple("project", "expl-12", "status", System.Text.Json.Nodes.JsonValue.Create(1))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "expl-12",
            Permission = "edit",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        var ttuNode = FindNode(result.Root, n => n.Type == CheckNodeType.TupleToUserSet && n.Name == "team.member");
        ttuNode.Should().NotBeNull();
        ttuNode!.Result.Should().BeTrue();
        ttuNode.Children.Count.Should().BeGreaterThanOrEqualTo(2, "one child per relation tuple in the parallel path");
    }

    protected static List<CheckNode> CollectAllNodes(CheckNode root)
    {
        var result = new List<CheckNode> { root };
        foreach (var child in root.Children)
            result.AddRange(CollectAllNodes(child));
        return result;
    }

    protected static CheckNode? FindNodeByType(CheckNode root, CheckNodeType type)
    {
        if (root.Type == type) return root;
        foreach (var child in root.Children)
        {
            var found = FindNodeByType(child, type);
            if (found is not null) return found;
        }
        return null;
    }

    protected static CheckNode? FindNode(CheckNode root, Func<CheckNode, bool> predicate)
    {
        if (predicate(root)) return root;
        foreach (var child in root.Children)
        {
            var found = FindNode(child, predicate);
            if (found is not null) return found;
        }
        return null;
    }
}
