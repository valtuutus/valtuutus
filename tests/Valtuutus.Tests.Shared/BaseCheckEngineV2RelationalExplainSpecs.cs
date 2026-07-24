using FluentAssertions;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Tests.Shared;

// Fusion (R1/R2/R4) only registers RelationalPlanRewriter on relational providers — this fact
// would fail on InMemory (BaseCheckEngineV2ExplainSpecs stays fusion-free for that reason;
// InMemory's CheckEngineV2ExplainSpecs inherits that base directly instead).
public abstract class BaseCheckEngineV2RelationalExplainSpecs : BaseCheckEngineV2ExplainSpecs
{
    protected BaseCheckEngineV2RelationalExplainSpecs(IDatabaseFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Explain_FusedUnion_ShowsFusedOpNode()
    {
        // view := (owner or admin or member) and isActiveStatus(status) — the 3-way sibling
        // union of direct relations is R1-fusable into one HasAnyOfDirectRelations call, but it
        // must be NESTED under something the rewriter can't also fold in (the function-call leaf
        // here has no FusedCheckLeaf representation, so GroupChildren's fullyRecognized flag goes
        // false for the outer "and" and the fusion stays partial). A bare
        // "view := owner or admin or member" fuses the ENTIRE permission body into one
        // PhysicalCheckNode at the plan ROOT; SpawnPlan's leaf-shape branch (V1 parity: root
        // keeps the caller's requested permission name) then overwrites only the root node's
        // Type, not its Name, so the fused node would surface with Name="view" instead of its
        // own Describe() text — indistinguishable from a coincidence rather than proof of fusion.
        // Nesting it here forces the fused sibling group to surface as an ordinary auto-derived
        // child (MakeExplainNode sets both Type AND Name from DescribeNode) instead.
        const string schema = """
            entity user {}
            entity doc {
                relation owner @user;
                relation admin @user;
                relation member @user;
                attribute status int;
                permission view := (owner or admin or member) and isActiveStatus(status);
            }
            fn isActiveStatus(status int) => status == 1;
            """;
        var engine = await CreateEngine(
            [new RelationTuple("doc", "v2-expl-fused1", "member", "user", "alice")],
            [new AttributeTuple("doc", "v2-expl-fused1", "status", System.Text.Json.Nodes.JsonValue.Create(1))],
            schema);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "doc", EntityId = "v2-expl-fused1",
            Permission = "view",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        var fusedNode = FindNode(result.Root, n => n.Type == CheckNodeType.FusedOp);
        fusedNode.Should().NotBeNull("R1 fuses this 3-way union into one provider call on a relational provider");
        fusedNode!.Children.Should().BeEmpty("a fused op is one opaque round trip, not an expanded subtree");
        fusedNode.Name.Should().Contain("HasAnyOfDirectRelations", "the node's Name comes from ICheckOp.Describe()");
        fusedNode.Result.Should().BeTrue();
    }
}
