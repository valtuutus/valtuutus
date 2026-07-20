using FluentAssertions;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Lang.SchemaReaders;
using Valtuutus.Core.Schemas;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

public class RelationalPlanRewriterSpecs
{
    internal static Schema Parse(string text)
    {
        var result = new SchemaReader(null).Parse(text);
        if (result.IsT1) throw new InvalidOperationException(string.Join(",", result.AsT1));
        return result.AsT0;
    }

    private const string HouseholdSchema = """
        entity user {}
        entity organization { relation admin @user; }
        entity household {
            relation parent @organization;
            relation owner @user;
            relation admin @user;
            relation member @user;
            permission view := owner or admin or member;
            permission escalate := owner or parent.admin;
        }
        """;

    [Fact]
    public void MultiDirect_rewrites_to_a_physical_HasAnyOfDirectRelations_op()
    {
        var plan = PlanCompiler.Compile(Parse(HouseholdSchema), "household", "view", "user");
        var rewritten = new RelationalPlanRewriter().Rewrite(plan.Root, Parse(HouseholdSchema));
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("HasAnyOfDirectRelations([owner, admin, member], any)");
    }

    [Fact]
    public void MultiAttribute_rewrites_to_a_physical_HasAnyOfAttributes_op()
    {
        const string s = """
            entity user {}
            entity doc {
                attribute a0 bool;
                attribute a1 bool;
                attribute a2 bool;
                permission view := a0 or a1 or a2;
            }
            """;
        var schema = Parse(s);
        var plan = PlanCompiler.Compile(schema, "doc", "view", "user");
        var rewritten = new RelationalPlanRewriter().Rewrite(plan.Root, schema);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("HasAnyOfAttributes([a0, a1, a2], any)");
    }

    [Fact]
    public void Unrecognized_nodes_pass_through_as_the_same_instance()
    {
        // escalate := owner or parent.admin — one batchable ref + a TTU: nothing groups, nothing
        // is recognizable, and the contract says untouched trees come back reference-identical.
        var plan = PlanCompiler.Compile(Parse(HouseholdSchema), "household", "escalate", "user");
        var rewritten = new RelationalPlanRewriter().Rewrite(plan.Root, Parse(HouseholdSchema));
        rewritten.Should().BeSameAs(plan.Root);
    }

    [Fact]
    public void MemoNode_wrapper_survives_a_rewrite_with_its_slot_intact()
    {
        // Shared `owner` forces a MemoNode; the batchable pair {admin, member} under the union
        // becomes a MultiDirect which the rewriter fuses — the MemoNode must keep its SlotId
        // and must NOT be unwrapped (fusion barrier).
        const string s = """
            entity user {}
            entity vault {
                relation owner @user;
                relation admin @user;
                relation member @user;
                permission open := (admin or member or owner) and not(owner);
            }
            """;
        var schema = Parse(s);
        var plan = PlanCompiler.Compile(schema, "vault", "open", "user");
        var rewritten = new RelationalPlanRewriter().Rewrite(plan.Root, schema);

        var intersect = rewritten.Should().BeOfType<IntersectNode>().Subject;
        var union = intersect.Children[0].Should().BeOfType<UnionNode>().Subject;
        union.Children.OfType<PhysicalCheckNode>().Should().ContainSingle()
            .Which.Op.Describe().Should().Be("HasAnyOfDirectRelations([admin, member], any)");
        var memo = union.Children.OfType<MemoNode>().Should().ContainSingle().Subject;
        var negate = intersect.Children[1].Should().BeOfType<NegateNode>().Subject;
        negate.Child.Should().BeOfType<MemoNode>().Which.SlotId.Should().Be(memo.SlotId);
    }
}
