using System.Collections.Immutable;
using FluentAssertions;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Lang.SchemaReaders;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.InMemory.Tests.V2;

public class PlanCompilerSpecs
{
    internal static Schema Parse(string text)
    {
        var result = new SchemaReader(null).Parse(text);
        if (result.IsT1) throw new InvalidOperationException(string.Join(",", result.AsT1));
        return result.AsT0;
    }

    [Fact]
    public void StructuralComparer_treats_equal_unions_with_distinct_arrays_as_equal()
    {
        // ImmutableArray<T> record equality is reference-based on the underlying array —
        // the comparer must recurse structurally or hash-consing silently never fires.
        var a = new UnionNode([new PlanRefNode("owner"), new PlanRefNode("editor")]);
        var b = new UnionNode([new PlanRefNode("owner"), new PlanRefNode("editor")]);
        PlanNodeStructuralComparer.Instance.Equals(a, b).Should().BeTrue();
        PlanNodeStructuralComparer.Instance.GetHashCode(a).Should().Be(PlanNodeStructuralComparer.Instance.GetHashCode(b));
    }

    [Fact]
    public void StructuralComparer_distinguishes_different_children()
    {
        var a = new UnionNode([new PlanRefNode("owner")]);
        var b = new UnionNode([new PlanRefNode("editor")]);
        PlanNodeStructuralComparer.Instance.Equals(a, b).Should().BeFalse();
    }

    private const string BasicSchema = """
        entity user {}
        entity organization {
            relation admin @user;
            relation member @user;
            attribute public bool;
            permission view := admin or member;
            permission manage := admin;
        }
        entity folder {
            relation parent @organization;
            relation owner @user;
            permission read := owner or parent.admin;
        }
        """;

    [Fact]
    public void Bare_relation_compiles_to_DirectRelationNode_root()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "organization", "admin", "user");
        plan.Root.Should().Be(new DirectRelationNode("admin", HasSubRelationPaths: false));
    }

    [Fact]
    public void Bare_attribute_compiles_to_AttributeTruthNode_root()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "organization", "public", "user");
        plan.Root.Should().Be(new AttributeTruthNode("public"));
    }

    [Fact]
    public void Unknown_name_compiles_to_ConstFalse()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "organization", "nope", "user");
        plan.Root.Should().Be(ConstNode.False);
    }

    [Fact]
    public void Union_permission_compiles_to_UnionNode_of_PlanRefs()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "organization", "view", null);
        var union = plan.Root.Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(2);
        union.Children[0].Should().Be(new PlanRefNode("admin"));
        union.Children[1].Should().Be(new PlanRefNode("member"));
    }

    [Fact]
    public void Single_leaf_permission_compiles_to_PlanRef()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "organization", "manage", "user");
        plan.Root.Should().Be(new PlanRefNode("admin"));
    }

    [Fact]
    public void Ttu_leaf_meeting_all_fast_path_conditions_is_annotated_with_FastPathSubEntityType()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "folder", "read", "user");
        var union = plan.Root.Should().BeOfType<UnionNode>().Subject;
        union.Children[1].Should().Be(
            new TupleToUserSetNode("parent", "admin", FastPathSubEntityType: "organization"));
    }

    [Fact]
    public void Ttu_leaf_with_null_subjectType_is_never_annotated()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "folder", "read", null);
        var union = plan.Root.Should().BeOfType<UnionNode>().Subject;
        union.Children[1].Should().Be(new TupleToUserSetNode("parent", "admin"));
    }

    [Fact]
    public void Ttu_leaf_with_multiple_tupleset_entities_is_not_fast_path_eligible()
    {
        // The schema DSL requires a relation's fully-resolved final entity references to be
        // consistent, so two *unrelated* direct entity types on one relation can't parse. To get
        // TuplesetRelation.Entities.Count > 1 while satisfying that, one entry is direct
        // (organization) and the other is a userset (group#member) that itself resolves down to
        // organization — still two Entities, still not fast-path eligible.
        const string s = """
            entity user {}
            entity organization {
                relation admin @user;
            }
            entity group {
                relation member @organization;
            }
            entity folder {
                relation parent @organization @group#member;
                permission read := parent.admin;
            }
            """;
        var plan = PlanCompiler.Compile(Parse(s), "folder", "read", "user");
        plan.Root.Should().Be(new TupleToUserSetNode("parent", "admin"));
    }

    [Fact]
    public void Ttu_leaf_with_userset_typed_tupleset_target_is_not_fast_path_eligible()
    {
        // The schema parser resolves a TTU's computed relation against the tupleset relation's
        // fully-recursed final entity, which always bottoms out at a non-userset reference — so
        // `admin` must exist there. A self-referencing `viewers @organization` keeps that bottom
        // entity at organization (which has `admin`), while folder.parent's own (single) Entities
        // entry is still userset-typed (Relation "viewers" is non-null), which is the condition
        // under test.
        const string s = """
            entity user {}
            entity organization {
                relation viewers @organization;
                relation admin @user;
            }
            entity folder {
                relation parent @organization#viewers;
                permission read := parent.admin;
            }
            """;
        var plan = PlanCompiler.Compile(Parse(s), "folder", "read", "user");
        plan.Root.Should().Be(new TupleToUserSetNode("parent", "admin"));
    }

    [Fact]
    public void Ttu_leaf_whose_computed_relation_has_sub_relation_paths_is_not_fast_path_eligible()
    {
        const string s = """
            entity user {}
            entity group {
                relation member @user;
            }
            entity organization {
                relation admin @user @group#member;
            }
            entity folder {
                relation parent @organization;
                permission read := parent.admin;
            }
            """;
        var plan = PlanCompiler.Compile(Parse(s), "folder", "read", "user");
        plan.Root.Should().Be(new TupleToUserSetNode("parent", "admin"));
    }

    [Fact]
    public void Ttu_leaf_unreachable_for_subjectType_folds_to_ConstFalse()
    {
        // The TTU branch must be a non-root Union child, not the whole permission: a bare
        // `permission read := parent.admin;` root gets short-circuited to ConstNode.False by
        // Compile's pre-existing top-level reachability guard (PlanCompiler.cs:10-12) before
        // PruneAndFold/PruneTupleToUserSet ever run, which would test that guard instead of
        // PruneTupleToUserSet's own `if (!reachable) return ConstNode.False;` fold. Keeping
        // "owner" reachable for service_account keeps the top-level permission reachable, so
        // Compile descends into PruneAndFold and PruneTupleToUserSet independently folds the
        // dead "parent.admin" branch (organization.admin only admits user).
        const string s = """
            entity user {}
            entity service_account {}
            entity organization {
                relation admin @user;
                relation bot @service_account;
            }
            entity folder {
                relation owner @service_account;
                relation parent @organization;
                permission read := owner or parent.admin;
            }
            """;
        var plan = PlanCompiler.Compile(Parse(s), "folder", "read", "service_account");
        plan.Root.Should().Be(new PlanRefNode("owner"));
    }

    private const string PruneSchema = """
        entity user {}
        entity service_account {}
        entity organization {
            relation admin @user;
            relation bot @service_account;
            permission access := admin or bot;
        }
        """;

    [Fact]
    public void Statically_dead_union_branch_is_pruned()
    {
        var plan = PlanCompiler.Compile(Parse(PruneSchema), "organization", "access", "user");
        plan.Root.Should().Be(new PlanRefNode("admin")); // bot pruned, single-child union collapsed
    }

    [Fact]
    public void Unreachable_permission_folds_to_ConstFalse()
    {
        var plan = PlanCompiler.Compile(Parse(PruneSchema), "organization", "bot", "user");
        plan.Root.Should().Be(ConstNode.False);
    }

    [Fact]
    public void Null_subjectType_prunes_nothing()
    {
        var plan = PlanCompiler.Compile(Parse(PruneSchema), "organization", "access", null);
        plan.Root.Should().BeOfType<UnionNode>().Which.Children.Should().HaveCount(2);
    }

    [Fact]
    public void Intersect_with_dead_branch_folds_to_ConstFalse()
    {
        const string s = """
            entity user {}
            entity service_account {}
            entity organization {
                relation admin @user;
                relation bot @service_account;
                permission locked := admin and bot;
            }
            """;
        var plan = PlanCompiler.Compile(Parse(s), "organization", "locked", "user");
        plan.Root.Should().Be(ConstNode.False);
    }

    [Fact]
    public void Cache_returns_same_plan_instance_for_same_key()
    {
        var cache = new CheckPlanCache(Parse(BasicSchema));
        var a = cache.GetOrCompile("organization", "view", "user");
        var b = cache.GetOrCompile("organization", "view", "user");
        b.Should().BeSameAs(a);
    }

    [Fact]
    public void Cache_keys_include_subjectType()
    {
        var cache = new CheckPlanCache(Parse(PruneSchema));
        var forUser = cache.GetOrCompile("organization", "access", "user");
        var forBot = cache.GetOrCompile("organization", "access", "service_account");
        forBot.Should().NotBeSameAs(forUser);
        forUser.Root.Should().Be(new PlanRefNode("admin"));
        forBot.Root.Should().Be(new PlanRefNode("bot"));
    }

    [Fact]
    public void Repeated_reference_in_one_plan_shares_a_slot()
    {
        const string s = """
            entity user {}
            entity folder {
                relation owner @user;
                relation editor @user;
                relation auditor @user;
                permission admin := owner or (editor and owner);
            }
            """;
        var plan = PlanCompiler.Compile(Parse(s), "folder", "admin", "user");
        plan.SlotCount.Should().Be(1);
        var union = plan.Root.Should().BeOfType<UnionNode>().Subject;
        var first = union.Children[0].Should().BeOfType<MemoNode>().Subject;
        first.Child.Should().Be(new PlanRefNode("owner"));
        var inner = union.Children[1].Should().BeOfType<IntersectNode>().Subject;
        var second = inner.Children[1].Should().BeOfType<MemoNode>().Subject;
        second.SlotId.Should().Be(first.SlotId);
    }

    private const string HouseholdSchema = """
        entity user {}
        entity household {
            relation owner @user;
            relation admin @user;
            relation member @user;
            attribute open bool;
            permission view := owner or admin or member;
            permission manage := owner and admin;
            permission browse := owner or admin or open;
        }
        """;

    [Fact]
    public void Compiled_plan_keeps_sibling_refs_ungrouped()
    {
        // Sibling fusion is the relational rewriter's job now (RelationalPlanRewriterSpecs in
        // Valtuutus.Data.Db.Tests) — the compiler's output stays plain PlanRefNode siblings
        // even for a fully fusable union with a known subject type.
        var plan = PlanCompiler.Compile(Parse(HouseholdSchema), "household", "view", "user");
        var union = plan.Root.Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(3);
        union.Children.Should().AllBeOfType<PlanRefNode>();
    }

    private sealed class ConstOp(bool value) : ICheckOp
    {
        public ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx,
            string entityType, string entityId, CancellationToken ct) => new(value);
        public string Describe() => $"Const({value})";
    }

    private sealed class AttributeHijackingRewriter : IPlanRewriter
    {
        public PlanNode Rewrite(PlanNode root, Schema schema, string entityType, string? subjectType)
            => root is AttributeTruthNode ? new PhysicalCheckNode(new ConstOp(true)) : root;
    }

    [Fact]
    public void Cache_applies_registered_rewriters_to_compiled_plans()
    {
        var cache = new CheckPlanCache(Parse(BasicSchema), [new AttributeHijackingRewriter()]);
        var plan = cache.GetOrCompile("organization", "public", "user");
        var physical = plan.Root.Should().BeOfType<PhysicalCheckNode>().Subject;
        physical.Op.Describe().Should().Be("Const(True)");
    }

    [Fact]
    public void Cache_with_no_rewriters_leaves_plans_untouched()
    {
        var cache = new CheckPlanCache(Parse(BasicSchema));
        cache.GetOrCompile("organization", "public", "user").Root.Should().Be(new AttributeTruthNode("public"));
    }
}
