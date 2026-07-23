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
    public void Full_fusion_when_every_child_is_recognizable()
    {
        // household.mixed := owner or admin or parent.admin — owner/admin group into MultiDirect,
        // parent.admin is TTU-fast-path-eligible (organization.admin is a direct relation
        // admitting user) — every child recognized, 2 leaves, so the WHOLE union fuses, not just
        // the direct-relation pair.
        var rewritten = Rewrite(Parse(HouseholdSchema), "household", "mixed", "user", out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("FusedExpression([MultiDirect([owner, admin], any), Ttu(parent -> organization#admin)], any)");
    }

    [Fact]
    public void Full_fusion_when_attribute_and_direct_groups_are_the_only_children()
    {
        // household.browse := owner or admin or open — owner/admin group into MultiDirect,
        // open is a single attribute leaf. Both leaves are already-fused-or-singleton groups
        // with nothing left over, so the whole union fuses into one FusedExpressionOp instead
        // of staying two separate PhysicalCheckNode children of a Union.
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
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("FusedExpression([Attribute(open), MultiDirect([owner, admin], any)], any)");
    }

    [Fact]
    public void Direct_and_attribute_sibling_groups_both_fuse_under_one_parent()
    {
        // Both groups fuse AND the whole union collapses into one FusedExpressionOp (2 leaves,
        // full fusion) rather than staying two separate PhysicalCheckNode children of a Union.
        // Deterministic leaf order matches the pre-existing group-ordering contract: attribute
        // group first, direct-relation group second.
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
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("FusedExpression([MultiAttribute([a0, a1], any), MultiDirect([r0, r1], any)], any)");
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
    public void Single_direct_and_single_ttu_leaf_fuse_together()
    {
        // household.escalate := owner or parent.admin — one unfused Direct (below the >= 2 group
        // threshold) + one TTU-fast-path leaf. Neither alone is worth wrapping, but together they
        // fuse (mirrors benchmarks/Valtuutus.Benchmarks/schema.vtt's team.edit := org.admin or
        // owner) — 2 leaves, full fusion.
        var rewritten = Rewrite(Parse(HouseholdSchema), "household", "escalate", "user", out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("FusedExpression([Direct(owner), Ttu(parent -> organization#admin)], any)");
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

    private const string TeamSchema = """
        entity user {}
        entity organization { relation admin @user; }
        entity team {
            relation owner @user;
            relation member @user;
            relation banned @user;
            relation org @organization;
            permission edit := org.admin or owner;
            permission invite := org.admin and (owner or member);
            permission negate_sibling_batch := owner and member and not(banned);
        }
        """;

    [Fact]
    public void Ttu_and_direct_fuse_under_union()
    {
        var rewritten = Rewrite(Parse(TeamSchema), "team", "edit", "user", out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("FusedExpression([Direct(owner), Ttu(org -> organization#admin)], any)");
    }

    [Fact]
    public void Ttu_and_nested_multi_direct_fuse_under_intersect()
    {
        // org.admin and (owner or member): the outer Intersect's raw children are [Ttu,
        // Union(owner, member)] — NEITHER is a raw PlanRefNode, so the outer relations/attributes
        // lists stay empty; walking the second child fuses the inner union to
        // PhysicalCheckNode(MultiDirect) first, and TryRecognizeSingleLeaf picks that up as a
        // MultiDirect leaf. Both leaves land in singleLeaves in encounter order — Ttu first (it
        // was the first original child), MultiDirect second (unlike Full_fusion_when_every_
        // child_is_recognizable's household.mixed case above, where owner/admin ARE raw
        // PlanRefNode siblings at THIS level, so they populate the outer `relations` list
        // directly and the group-leaf-before-singleLeaves ordering rule applies instead).
        var rewritten = Rewrite(Parse(TeamSchema), "team", "invite", "user", out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("FusedExpression([Ttu(org -> organization#admin), MultiDirect([owner, member], any)], all)");
    }

    [Fact]
    public void Negated_single_leaf_fuses_alongside_a_multi_direct_group()
    {
        // owner and member and not(banned): {owner, member} group into MultiDirect(all);
        // not(banned) is a negated single Direct leaf — both recognized, 2 leaves, full fusion.
        var rewritten = Rewrite(Parse(TeamSchema), "team", "negate_sibling_batch", "user", out _);
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("FusedExpression([MultiDirect([owner, member], all), not Direct(banned)], all)");
    }

    [Fact]
    public void Full_fusion_does_not_fire_when_subject_type_unknown()
    {
        // Without subjectType: PlanCompiler.PruneTupleToUserSet returns early before ever setting
        // FastPathSubEntityType, so org.admin isn't recognized either — and directGate being
        // false means owner never enters the relations group. Both children stay unrecognized,
        // so nothing fuses at all (falls all the way through to RebuildIfChanged unchanged).
        var rewritten = Rewrite(Parse(TeamSchema), "team", "edit", null, out var compiledRoot);
        rewritten.Should().BeSameAs(compiledRoot);
    }

    [Fact]
    public void Nested_fused_expression_flattens_into_the_outer_fusion()
    {
        // A hand-built tree standing in for "(a or b or org.admin) or member", built directly
        // rather than through PlanCompiler.Compile: SchemaBuilder flattens any homogeneous
        // "or of or"/"and of and" chain in a permission's DSL text into one flat n-ary
        // Union/Intersect (PermissionNode.Flatten) BEFORE PlanCompiler.CompileTree ever runs —
        // confirmed by compiling this exact permission text and observing a/b/member all land in
        // one MultiDirect group with no nesting left for GroupChildren to recurse into. Only
        // heterogeneous nesting (mixed AND/OR, e.g. Ttu_and_nested_multi_direct_fuse_under_
        // intersect above) survives the schema compiler, and that always has a mismatched
        // combinator for this flatten guard (inner requireAll can only equal outer's !isUnion
        // when both levels share one combinator — which is exactly the case Flatten already
        // collapses). So the only way to exercise GroupChildren's own recursive recognition of a
        // nested FusedExpressionOp is a directly constructed PlanNode tree, which any provider
        // rewriter must still handle correctly regardless of how it got here.
        const string s = """
            entity user {}
            entity organization { relation admin @user; }
            entity team {
                relation a @user;
                relation b @user;
                relation member @user;
                relation org @organization;
            }
            """;
        var schema = Parse(s);
        var innerUnion = new UnionNode([
            new PlanRefNode("a"),
            new PlanRefNode("b"),
            new TupleToUserSetNode("org", "admin", FastPathSubEntityType: "organization"),
        ]);
        var outerUnion = new UnionNode([innerUnion, new PlanRefNode("member")]);

        // Walking the inner union first fuses it to PhysicalCheckNode(FusedExpressionOp(
        // [MultiDirect([a, b], any), Ttu(...)], requireAll: false)) — its own combinator (any,
        // from being a Union) matches what the outer Union needs, so the outer classification
        // loop splices those 2 leaves straight into singleLeaves instead of treating the whole
        // nested op as one opaque leaf. `member` is a plain PlanRefNode at the outer level, so it
        // populates the outer `relations` group (count 1) as its own Direct leaf, ordered before
        // the flattened leaves per the existing group-before-singleLeaves contract.
        var rewritten = new RelationalPlanRewriter().Rewrite(outerUnion, schema, "team", "user");
        var physical = rewritten.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be(
            "FusedExpression([Direct(member), MultiDirect([a, b], any), Ttu(org -> organization#admin)], any)");
    }
}
