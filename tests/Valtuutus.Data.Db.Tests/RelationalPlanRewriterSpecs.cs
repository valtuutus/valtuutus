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

    private static PlanNode Rewrite(Schema schema, string entityType, string permission, string? subjectType,
        out PlanNode compiledRoot)
    {
        var plan = PlanCompiler.Compile(schema, entityType, permission, subjectType);
        compiledRoot = plan.Root;
        return new RelationalPlanRewriter().Rewrite(plan.Root, schema, entityType, subjectType);
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
            permission manage := owner and admin;
            permission escalate := owner or parent.admin;
            permission mixed := owner or admin or parent.admin;
        }
        """;

    [Fact]
    public void Fuses_two_sibling_direct_relation_refs_under_union_into_physical_op()
    {
        const string s = """
            entity user {}
            entity team {
                relation owner @user;
                relation member @user;
                permission edit := owner or member;
            }
            """;
        var rewritten = Rewrite(Parse(s), "team", "edit", "user", out _);
        // Single-survivor collapse: both children fused, so the union unwraps entirely.
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("HasAnyOfDirectRelations([owner, member], any)");
    }

    [Fact]
    public void Fuses_three_sibling_direct_relation_refs_into_one_physical_op()
    {
        var rewritten = Rewrite(Parse(HouseholdSchema), "household", "view", "user", out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("HasAnyOfDirectRelations([owner, admin, member], any)");
    }

    [Fact]
    public void Does_not_fuse_when_subject_type_unknown()
    {
        // Mirror of V1's subjectTypeKnown gate (formerly compile-time in PlanCompiler): without
        // a subject type, direct-relation refs stay plain — and nothing changed, so the rewriter
        // must hand back the same instance.
        var rewritten = Rewrite(Parse(HouseholdSchema), "household", "view", null, out var compiledRoot);
        rewritten.Should().BeSameAs(compiledRoot);
        rewritten.Should().BeOfType<UnionNode>().Which.Children.Should().AllBeOfType<PlanRefNode>();
    }

    private const string AttributeSchema = """
        entity user {}
        entity doc {
            attribute a0 bool;
            attribute a1 bool;
            attribute a2 bool;
            permission view := a0 or a1 or a2;
        }
        """;

    [Fact]
    public void Attribute_truth_siblings_fuse_regardless_of_subject_type()
    {
        // R4 has no subjectType gate — bool attributes have no subject dependency at all.
        var rewritten = Rewrite(Parse(AttributeSchema), "doc", "view", null, out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("HasAnyOfAttributes([a0, a1, a2], any)");
    }

    [Fact]
    public void Intersect_siblings_fuse_with_require_all_semantics()
    {
        var rewritten = Rewrite(Parse(HouseholdSchema), "household", "manage", "user", out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("HasAnyOfDirectRelations([owner, admin], all)");
    }

    [Fact]
    public void Attribute_siblings_under_intersect_fuse_with_require_all()
    {
        const string s = """
            entity user {}
            entity doc {
                attribute a0 bool;
                attribute a1 bool;
                permission edit := a0 and a1;
            }
            """;
        var rewritten = Rewrite(Parse(s), "doc", "edit", null, out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("HasAnyOfAttributes([a0, a1], all)");
    }

    [Fact]
    public void Mixed_children_partial_fusion()
    {
        // Two batchable refs + one TTU: the refs fuse, the TTU child survives — fused node
        // first, then the non-batchable children in their original order, reference-identical.
        var rewritten = Rewrite(Parse(HouseholdSchema), "household", "mixed", "user", out var compiledRoot);
        var union = rewritten.Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(2);
        union.Children[0].Should().BeOfType<PhysicalCheckNode>()
            .Which.Op.Describe().Should().Be("HasAnyOfDirectRelations([owner, admin], any)");
        var originalTtu = compiledRoot.Should().BeOfType<UnionNode>().Subject.Children[^1];
        union.Children[1].Should().BeSameAs(originalTtu).And.BeOfType<TupleToUserSetNode>();
    }

    [Fact]
    public void Single_attribute_sibling_stays_when_only_the_direct_pair_fuses()
    {
        // One attribute ref is below the >= 2 threshold for its own group — it stays a plain
        // walked sibling next to the fused direct-relation pair.
        const string s = """
            entity user {}
            entity household {
                relation owner @user;
                relation admin @user;
                attribute open bool;
                permission browse := owner or admin or open;
            }
            """;
        var rewritten = Rewrite(Parse(s), "household", "browse", "user", out _);
        var union = rewritten.Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(2);
        union.Children[0].Should().BeOfType<PhysicalCheckNode>()
            .Which.Op.Describe().Should().Be("HasAnyOfDirectRelations([owner, admin], any)");
        union.Children[1].Should().Be(new PlanRefNode("open"));
    }

    [Fact]
    public void Direct_and_attribute_sibling_groups_both_fuse_under_one_parent()
    {
        // Both groups may fire under the same parent. The rewritten plan's shape is
        // deterministic and contractual: fused attribute group first, fused direct group
        // second, unfused children in original order. (Same shape the old sequential compiler
        // passes produced: directs grouped first, attributes then prepended.)
        const string s = """
            entity user {}
            entity doc {
                relation r0 @user;
                relation r1 @user;
                attribute a0 bool;
                attribute a1 bool;
                permission view := r0 or r1 or a0 or a1;
            }
            """;
        var rewritten = Rewrite(Parse(s), "doc", "view", "user", out _);
        var union = rewritten.Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(2);
        union.Children[0].Should().BeOfType<PhysicalCheckNode>()
            .Which.Op.Describe().Should().Be("HasAnyOfAttributes([a0, a1], any)");
        union.Children[1].Should().BeOfType<PhysicalCheckNode>()
            .Which.Op.Describe().Should().Be("HasAnyOfDirectRelations([r0, r1], any)");
    }

    [Fact]
    public void Attribute_group_fuses_and_direct_refs_stay_when_subject_type_unknown()
    {
        const string s = """
            entity user {}
            entity doc {
                relation r0 @user;
                relation r1 @user;
                attribute a0 bool;
                attribute a1 bool;
                permission view := r0 or r1 or a0 or a1;
            }
            """;
        var rewritten = Rewrite(Parse(s), "doc", "view", null, out _);
        var union = rewritten.Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(3);
        union.Children[0].Should().BeOfType<PhysicalCheckNode>()
            .Which.Op.Describe().Should().Be("HasAnyOfAttributes([a0, a1], any)");
        union.Children[1].Should().Be(new PlanRefNode("r0"));
        union.Children[2].Should().Be(new PlanRefNode("r1"));
    }

    [Fact]
    public void Sub_relation_path_refs_are_not_fused()
    {
        // V1 parity: IsBatchableDirectRelation excludes HasSubRelationPaths — those leaves
        // still need the GetIndirectRelations fan-out.
        const string s = """
            entity user {}
            entity team { relation member @user; }
            entity doc {
                relation viewer @user @team#member;
                relation editor @user @team#member;
                permission read := viewer or editor;
            }
            """;
        var rewritten = Rewrite(Parse(s), "doc", "read", "user", out var compiledRoot);
        rewritten.Should().BeSameAs(compiledRoot);
        rewritten.Should().BeOfType<UnionNode>().Which.Children.Should().AllBeOfType<PlanRefNode>();
    }

    [Fact]
    public void Unrecognized_nodes_pass_through_as_the_same_instance()
    {
        // escalate := owner or parent.admin — one batchable ref + a TTU: below the >= 2
        // threshold nothing fuses, and the contract says untouched trees come back
        // reference-identical.
        var rewritten = Rewrite(Parse(HouseholdSchema), "household", "escalate", "user", out var compiledRoot);
        rewritten.Should().BeSameAs(compiledRoot);
    }

    [Fact]
    public void Memo_wrapped_sibling_is_never_grouped()
    {
        // Shared `owner` is hash-consed into a MemoNode, which fails the PlanRefNode type test —
        // that non-match IS the fusion barrier. The batchable pair {admin, member} still fuses,
        // and the MemoNode must survive un-fused with its SlotId intact, as ONE instance shared
        // with the negate branch (DAG preserved).
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
        var rewritten = new RelationalPlanRewriter().Rewrite(plan.Root, schema, "vault", "user");

        var intersect = rewritten.Should().BeOfType<IntersectNode>().Subject;
        var union = intersect.Children[0].Should().BeOfType<UnionNode>().Subject;
        union.Children.OfType<PhysicalCheckNode>().Should().ContainSingle()
            .Which.Op.Describe().Should().Be("HasAnyOfDirectRelations([admin, member], any)");
        var memo = union.Children.OfType<MemoNode>().Should().ContainSingle().Subject;
        var negate = intersect.Children[1].Should().BeOfType<NegateNode>().Subject;
        negate.Child.Should().BeSameAs(memo);
        memo.Child.Should().Be(new PlanRefNode("owner"));
    }

    private const string UsersetJoinSchema = """
        entity user {}
        entity group { relation member @user; }
        entity folder {
            relation owner @user @group#member;
        }
        """;

    [Fact]
    public void Eligible_direct_relation_userset_target_rewrites_to_physical_userset_join()
    {
        var rewritten = Rewrite(Parse(UsersetJoinSchema), "folder", "owner", "user", out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("UsersetJoin(owner -> group#member)");
    }

    [Fact]
    public void Direct_relation_without_userset_target_passes_through_unchanged()
    {
        const string s = """
            entity user {}
            entity folder { relation owner @user; }
            """;
        var rewritten = Rewrite(Parse(s), "folder", "owner", "user", out var compiledRoot);
        rewritten.Should().BeSameAs(compiledRoot);
        rewritten.Should().BeOfType<DirectRelationNode>();
    }

    [Fact]
    public void Direct_relation_userset_target_stays_unfused_when_subject_type_unknown()
    {
        // PruneDirectRelationUserSet needs a known subjectType — without one,
        // FastPathSubEntityType is never set, so the rewriter has nothing to recognize.
        var rewritten = Rewrite(Parse(UsersetJoinSchema), "folder", "owner", null, out var compiledRoot);
        rewritten.Should().BeSameAs(compiledRoot);
        rewritten.Should().BeOfType<DirectRelationNode>();
    }
}
