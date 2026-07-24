using FluentAssertions;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Tests.Shared;

// Facts here assert V1-specific behavior that R1/R2's boolean-combination/userset-join fusion
// (Valtuutus.Data.Db's RelationalPlanRewriter, shipped independently of this explain-parity
// effort) can legitimately make untrue for a fusing V2 provider (Postgres/SqlServer) — either an
// always-on V1 optimization V2 has no equivalent for (batching), or a granular per-leaf tree shape
// that fusion can legitimately collapse/restructure (attribute+relation mixes, RBAC
// tuple-to-userset relations, etc. — confirmed by tracing RelationalPlanRewriter's rewrite rules,
// not assumed). These do not belong on BaseCheckEngineExplainSpecs, which both V1 and every V2
// test base (including the fusing-provider ones) inherit — putting V1-only assertions there was
// the root cause of explain failures under a fusing V2 provider. V1's own concrete classes
// (Valtuutus.Data.{InMemory,Postgres,SqlServer}.Tests.CheckEngineExplainSpecs) inherit this
// instead of BaseCheckEngineExplainSpecs directly; no V2 test base ever inherits this.
public abstract class BaseCheckEngineV1OnlyExplainSpecs : BaseCheckEngineExplainSpecs
{
    protected BaseCheckEngineV1OnlyExplainSpecs(IDatabaseFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Explain_UnionExpression_ShortCircuitsAfterFirstTrue()
    {
        // view := public or owner or admin or member
        // alice is owner; public=false
        var engine = await CreateEngine(
            [new RelationTuple("workspace", "expl-3", "owner", "user", "alice")],
            [new AttributeTuple("workspace", "expl-3", "public", System.Text.Json.Nodes.JsonValue.Create(false))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-3",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        result.Root.Name.Should().Be("view");

        // Grammar parses "a or b or c or d" as a left-associative binary tree.
        // The owner node must appear somewhere in the tree.
        // We don't assert owner.Result because with real async DB backends the owner
        // branch may be cancelled before its query returns if another branch wins the race first.
        var ownerNode = FindNode(result.Root, n => n.Name == "owner");
        ownerNode.Should().NotBeNull("owner relation should be present in tree");

        // Public attribute evaluated to false (alice is not public-access).
        // We search by name only: if the branch was cancelled before CheckAttribute ran,
        // the node type stays as Permission (set by GetNodeInfo) instead of Attribute.
        var publicAttrNode = FindNode(result.Root, n => n.Name == "public");
        publicAttrNode.Should().NotBeNull("public node should exist in tree");
        publicAttrNode!.Result.Should().BeFalse();
    }

    [Fact]
    public async Task Explain_UnionExpression_BatchedSiblingRelations_ShowBatchedDetail()
    {
        // view := public or owner or admin or member — owner/admin/member are all direct
        // relations on workspace, batchable together via HasAnyOfDirectRelations. No tuples
        // and public=false means the whole union is false, so nothing short-circuits and every
        // sibling's Detail is deterministic (unlike the short-circuit test above). V1-only: an
        // always-on optimization (CheckEngine.cs's IsBatchableDirectRelation/
        // ResolveBatchedRelation) with no V2 equivalent on any provider.
        var engine = await CreateEngine(
            [],
            [new AttributeTuple("workspace", "expl-batch-1", "public", System.Text.Json.Nodes.JsonValue.Create(false))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-batch-1",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeFalse();

        var ownerNode = FindNode(result.Root, n => n.Name == "owner");
        var adminNode = FindNode(result.Root, n => n.Name == "admin");
        var memberNode = FindNode(result.Root, n => n.Name == "member");

        ownerNode.Should().NotBeNull();
        adminNode.Should().NotBeNull();
        memberNode.Should().NotBeNull();

        ownerNode!.Detail.Should().Be("batched: no matching tuple");
        adminNode!.Detail.Should().Be("batched: no matching tuple");
        memberNode!.Detail.Should().Be("batched: no matching tuple");
        ownerNode.Result.Should().BeFalse();
        adminNode.Result.Should().BeFalse();
        memberNode.Result.Should().BeFalse();
    }

    [Fact]
    public async Task Explain_IntersectExpression_ReturnsFalseWhenFirstChildFalse()
    {
        // comment := member and public (binary intersect — exactly 2 children)
        // alice is NOT a member; public=true
        var engine = await CreateEngine(
            [],
            [new AttributeTuple("workspace", "expl-4", "public", System.Text.Json.Nodes.JsonValue.Create(true))]);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-4",
            Permission = "comment",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeFalse();
        result.Root.Name.Should().Be("comment");
        // Intersect wraps children under a single "and" expression node
        result.Root.Children.Should().HaveCount(1);
        result.Root.Children[0].Name.Should().Be("and");
        result.Root.Children[0].Type.Should().Be(CheckNodeType.Expression);
        // member resolves false (no tuple), public resolves true or short-circuited
        result.Root.Children[0].Children.Should().Contain(n => n.Name == "member" && !n.Result);
    }

    [Fact]
    public async Task Explain_AttributeCheck_ReturnsAttributeNodeWithDetail()
    {
        var engine = await CreateEngine(
            [],
            [new AttributeTuple("workspace", "expl-5", "public", System.Text.Json.Nodes.JsonValue.Create(true))]);

        // view := public or owner or admin or member — public is true
        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-5",
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
    public async Task Explain_MissingAttribute_ReturnsAttributeFalseDetail()
    {
        // public attribute not seeded — CheckAttribute gets null from the reader
        var engine = await CreateEngine([], []);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "workspace", EntityId = "expl-attr1",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeFalse();
        var publicNode = FindNode(result.Root, n => n.Type == CheckNodeType.Attribute && n.Name == "public");
        publicNode.Should().NotBeNull("public attribute node should appear in tree");
        publicNode!.Detail.Should().Be("attribute=False");
    }

    [Fact]
    public async Task Explain_UnionFailed_NoDuplicatedNodes()
    {
        // read := viewer or editor or admin — three-way union, none granted
        const string schema = """
            entity user {}
            entity resource {
                relation viewer @user;
                relation editor @user;
                relation admin @user;
                permission read := viewer or editor or admin;
            }
            """;
        var engine = await CreateEngine([], [], schema);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "resource", EntityId = "res-1",
            Permission = "read",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeFalse();

        // Each relation should appear exactly once — no duplicate nodes with the same name.
        var allNodes = CollectAllNodes(result.Root);
        var nameGroups = allNodes.GroupBy(n => n.Name).Where(g => g.Count() > 1).ToList();
        nameGroups.Should().BeEmpty("each relation name should appear at most once in the tree");

        // All three leaf relations must be present and failed.
        foreach (var rel in new[] { "viewer", "editor", "admin" })
        {
            var node = FindNode(result.Root, n => n.Name == rel);
            node.Should().NotBeNull($"{rel} node should exist in tree");
            node!.Result.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Explain_RbacTupleToUserSet_ShowsRoleChildNodes()
    {
        // RBAC schema: admin/editor/viewer are @role#assignee (tuple-to-userset)
        // alice is assignee of admin_role, which has admin on resource:api
        const string rbacSchema = """
            entity user {}
            entity role {
                relation assignee @user;
            }
            entity resource {
                relation admin  @role#assignee;
                relation editor @role#assignee;
                relation viewer @role#assignee;
                permission manage := admin;
                permission write  := editor or admin;
                permission read   := viewer or editor or admin;
            }
            """;
        var engine = await CreateEngine(
            [
                new RelationTuple("role", "admin_role", "assignee", "user", "alice"),
                new RelationTuple("role", "editor_role", "assignee", "user", "bob"),
                new RelationTuple("role", "viewer_role", "assignee", "user", "charlie"),
                new RelationTuple("resource", "api", "admin", "role", "admin_role", "assignee"),
                new RelationTuple("resource", "api", "editor", "role", "editor_role", "assignee"),
                new RelationTuple("resource", "api", "viewer", "role", "viewer_role", "assignee"),
            ],
            [], rbacSchema);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "resource", EntityId = "api",
            Permission = "read",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();

        // admin is a plain relation reference (not TTU notation), so its node type is Relation.
        // It resolves via the indirect sub-relation path: admin @role#assignee → checks role:admin_role#assignee.
        var adminNode = FindNode(result.Root, n => n.Name == "admin" && n.Type == CheckNodeType.Relation);
        adminNode.Should().NotBeNull("admin Relation node should exist");
        adminNode!.Children.Should().NotBeEmpty("admin should have a child showing role:admin_role was checked via sub-relation path");

        var childOfAdmin = adminNode.Children[0];
        childOfAdmin.EntityType.Should().Be("role");
        childOfAdmin.EntityId.Should().Be("admin_role");
        childOfAdmin.Result.Should().BeTrue();
    }

}
