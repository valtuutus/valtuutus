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

    private async ValueTask<ICheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        string? schema = null)
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(schema ?? TestsConsts.DefaultSchema);
        AddSpecificProvider(services).AddConcurrentQueryLimit(3);
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
            [new RelationTuple("workspace", "1", "owner", "user", "alice")],
            []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "1",
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
            EntityType = "workspace", EntityId = "1",
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
    public async Task Explain_UnionExpression_ShortCircuitsAfterFirstTrue()
    {
        // view := public or owner or admin or member
        // alice is owner; public=false
        var engine = await CreateEngine(
            [new RelationTuple("workspace", "1", "owner", "user", "alice")],
            [new AttributeTuple("workspace", "1", "public", System.Text.Json.Nodes.JsonValue.Create(false))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "1",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        result.Root.Name.Should().Be("view");

        // Grammar parses "a or b or c or d" as a left-associative binary tree,
        // so we verify the key nodes exist somewhere in the tree.
        var ownerNode = FindNode(result.Root, n => n.Name == "owner" && n.Result);
        ownerNode.Should().NotBeNull("owner relation should be found and true");

        var publicAttrNode = FindNode(result.Root, n => n.Type == CheckNodeType.Attribute && n.Name == "public");
        publicAttrNode.Should().NotBeNull("public attribute node should exist in tree");
        publicAttrNode!.Result.Should().BeFalse();
    }

    [Fact]
    public async Task Explain_IntersectExpression_ReturnsFalseWhenFirstChildFalse()
    {
        // comment := member and public (binary intersect — exactly 2 children)
        // alice is NOT a member; public=true
        var engine = await CreateEngine(
            [],
            [new AttributeTuple("workspace", "1", "public", System.Text.Json.Nodes.JsonValue.Create(true))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "1",
            Permission = "comment",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeFalse();
        result.Root.Name.Should().Be("comment");
        // Binary parse: exactly 2 top-level children
        result.Root.Children.Should().HaveCount(2);
        // member resolves false (no tuple), public resolves true or short-circuited
        result.Root.Children.Should().Contain(n => n.Name == "member" && !n.Result);
    }

    [Fact]
    public async Task Explain_AttributeCheck_ReturnsAttributeNodeWithDetail()
    {
        var engine = await CreateEngine(
            [],
            [new AttributeTuple("workspace", "1", "public", System.Text.Json.Nodes.JsonValue.Create(true))]);

        // view := public or owner or admin or member — public is true
        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "1",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        // The attribute node is nested inside the binary expression tree
        var publicNode = FindNode(result.Root, n => n.Type == CheckNodeType.Attribute && n.Name == "public");
        publicNode.Should().NotBeNull();
        publicNode!.Detail.Should().Be("attribute=True");
        publicNode.Result.Should().BeTrue();
    }

    [Fact]
    public async Task Explain_FunctionCall_ReturnsFunctionNodeWithDetail()
    {
        // project.edit := (parent.admin or team.member) and isActiveStatus(status)
        // Seed: alice is parent workspace admin AND status=1
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "1", "parent", "workspace", "ws1"),
                new RelationTuple("workspace", "ws1", "admin", "user", "alice")
            ],
            [new AttributeTuple("project", "1", "status", System.Text.Json.Nodes.JsonValue.Create(1))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "1",
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
                new RelationTuple("project", "1", "parent", "workspace", "ws1"),
                new RelationTuple("workspace", "ws1", "admin", "user", "alice")
            ],
            [new AttributeTuple("project", "1", "status", System.Text.Json.Nodes.JsonValue.Create(1))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "1",
            Permission = "edit",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        var ttuNode = FindNodeByType(result.Root, CheckNodeType.TupleToUserSet);
        ttuNode.Should().NotBeNull();
        ttuNode!.Result.Should().BeTrue();
    }

    [Fact]
    public async Task Explain_NoInfiniteRecursion_ReturnsFiniteTree()
    {
        var engine = await CreateEngine(
            [new RelationTuple("project", "1", "member", "user", "alice")],
            []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "project", EntityId = "1",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        result.Root.Name.Should().Be("view");
    }

    private static CheckNode? FindNodeByType(CheckNode root, CheckNodeType type)
    {
        if (root.Type == type) return root;
        foreach (var child in root.Children)
        {
            var found = FindNodeByType(child, type);
            if (found is not null) return found;
        }
        return null;
    }

    private static CheckNode? FindNode(CheckNode root, Func<CheckNode, bool> predicate)
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
