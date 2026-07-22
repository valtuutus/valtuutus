using FluentAssertions;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

public class PlanValidatorSpecs
{
    [Fact]
    public void Accepts_a_well_formed_compiled_plan()
    {
        // The repeated (owner and admin) subtree is hash-consed into a shared MemoNode,
        // so this plan exercises the memo-identity rules the validator exists to enforce.
        const string s = """
            entity user {}
            entity household {
                relation owner @user;
                relation admin @user;
                relation member @user;
                permission mixed := (owner and admin) or ((owner and admin) and member);
            }
            """;
        var schema = RelationalPlanRewriterSpecs.Parse(s);
        var plan = PlanCompiler.Compile(schema, "household", "mixed", "user");
        plan.SlotCount.Should().BeGreaterThan(0, "the spec must exercise a memo-carrying plan");
        var rewritten = new RelationalPlanRewriter().Rewrite(plan.Root, schema, "household", "user");

        var act = () => PlanValidator.Validate(rewritten, plan.SlotCount);
        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_shared_memo_instance_reachable_via_multiple_parents()
    {
        // The legal DAG case: ONE MemoNode instance under two parents. A naive
        // "seen this slot already" check would wrongly reject this.
        var shared = new MemoNode(0, new PlanRefNode("a"));
        var root = new UnionNode([new IntersectNode([shared, new PlanRefNode("b")]), shared]);

        var act = () => PlanValidator.Validate(root, slotCount: 1);
        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_duplicate_memo_slot_instances()
    {
        var dup1 = new MemoNode(0, new PlanRefNode("a"));
        var dup2 = new MemoNode(0, new PlanRefNode("a"));
        var root = new UnionNode([dup1, dup2]);

        var act = () => PlanValidator.Validate(root, slotCount: 1);
        act.Should().Throw<InvalidOperationException>().WithMessage("*two distinct MemoNode instances*");
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void Rejects_slot_id_out_of_range(int slotId)
    {
        var root = new MemoNode(slotId, new PlanRefNode("a"));

        var act = () => PlanValidator.Validate(root, slotCount: 1);
        act.Should().Throw<InvalidOperationException>().WithMessage("*out of range*");
    }

    [Fact]
    public void Rejects_null_op_in_physical_node()
    {
        var act = () => PlanValidator.Validate(new PhysicalCheckNode(null!), slotCount: 0);
        act.Should().Throw<InvalidOperationException>().WithMessage("*without an ICheckOp*");
    }

    [Fact]
    public void Rejects_direct_relation_node_below_root()
    {
        var root = new UnionNode([new DirectRelationNode("owner", false)]);

        var act = () => PlanValidator.Validate(root, slotCount: 0);
        act.Should().Throw<InvalidOperationException>().WithMessage("*root-only*");
    }

    [Fact]
    public void Rejects_attribute_truth_node_below_root()
    {
        var root = new NegateNode(new AttributeTruthNode("archived"));

        var act = () => PlanValidator.Validate(root, slotCount: 0);
        act.Should().Throw<InvalidOperationException>().WithMessage("*root-only*");
    }
}
