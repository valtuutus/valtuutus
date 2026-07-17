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
        // Grouping fuses the two batchable refs; compile with an unknown subject type to see
        // the ungrouped logical shape (grouping requires subjectTypeKnown, V1 parity).
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "organization", "view", null);
        var union = plan.Root.Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(2);
        union.Children[0].Should().Be(new PlanRefNode("admin"));
        union.Children[1].Should().Be(new PlanRefNode("member"));
    }

    [Fact]
    public void Union_permission_groups_for_known_subjectType()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "organization", "view", "user");
        var multi = plan.Root.Should().BeOfType<MultiDirectNode>().Subject;
        multi.Relations.Should().Equal("admin", "member");
    }

    [Fact]
    public void Single_leaf_permission_compiles_to_PlanRef()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "organization", "manage", "user");
        plan.Root.Should().Be(new PlanRefNode("admin"));
    }

    [Fact]
    public void Ttu_leaf_compiles_to_TupleToUserSetNode()
    {
        var plan = PlanCompiler.Compile(Parse(BasicSchema), "folder", "read", "user");
        var union = plan.Root.Should().BeOfType<UnionNode>().Subject;
        union.Children[1].Should().Be(new TupleToUserSetNode("parent", "admin"));
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
    public void Union_of_batchable_refs_groups_to_MultiDirect()
    {
        var plan = PlanCompiler.Compile(Parse(HouseholdSchema), "household", "view", "user");
        var multi = plan.Root.Should().BeOfType<MultiDirectNode>().Subject;
        multi.Relations.Should().Equal("owner", "admin", "member");
        multi.RequireAll.Should().BeFalse();
    }

    [Fact]
    public void Intersect_of_batchable_refs_groups_with_RequireAll()
    {
        var plan = PlanCompiler.Compile(Parse(HouseholdSchema), "household", "manage", "user");
        var multi = plan.Root.Should().BeOfType<MultiDirectNode>().Subject;
        multi.Relations.Should().Equal("owner", "admin");
        multi.RequireAll.Should().BeTrue();
    }

    [Fact]
    public void Mixed_union_groups_only_the_batchable_refs()
    {
        // `open` is an attribute → PlanRefNode("open") is not a direct relation, stays a sibling.
        var plan = PlanCompiler.Compile(Parse(HouseholdSchema), "household", "browse", "user");
        var union = plan.Root.Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(2);
        var multi = union.Children[0].Should().BeOfType<MultiDirectNode>().Subject;
        multi.Relations.Should().Equal("owner", "admin");
        union.Children[1].Should().Be(new PlanRefNode("open"));
    }

    [Fact]
    public void Null_subjectType_skips_grouping()
    {
        // V1 parity: batching requires subjectTypeKnown (CheckEngine.cs:564).
        var plan = PlanCompiler.Compile(Parse(HouseholdSchema), "household", "view", null);
        plan.Root.Should().BeOfType<UnionNode>().Which.Children.Should().AllBeOfType<PlanRefNode>();
    }

    [Fact]
    public void Sub_relation_path_refs_are_not_grouped()
    {
        // V1 parity: IsBatchableDirectRelation excludes HasSubRelationPaths (CheckEngine.cs:420) —
        // those leaves still need the GetIndirectRelations fan-out.
        const string s = """
            entity user {}
            entity team { relation member @user; }
            entity doc {
                relation viewer @user @team#member;
                relation editor @user @team#member;
                permission read := viewer or editor;
            }
            """;
        var plan = PlanCompiler.Compile(Parse(s), "doc", "read", "user");
        plan.Root.Should().BeOfType<UnionNode>().Which.Children.Should().AllBeOfType<PlanRefNode>();
    }

    [Fact]
    public void Memo_wrapped_ref_stays_outside_the_batch()
    {
        // `owner` is referenced twice → hash-consing wraps it in a MemoNode. The MemoNode fails
        // the PlanRefNode type test, so the union has only ONE plain batchable ref (`admin`) —
        // below threshold, nothing groups. That non-match is the fusion barrier working.
        const string s = """
            entity user {}
            entity vault {
                relation owner @user;
                relation admin @user;
                permission locked := (owner or admin) and not(owner);
            }
            """;
        var plan = PlanCompiler.Compile(Parse(s), "vault", "locked", "user");
        var intersect = plan.Root.Should().BeOfType<IntersectNode>().Subject;
        var union = intersect.Children[0].Should().BeOfType<UnionNode>().Subject;
        union.Children.Should().HaveCount(2);
        union.Children.OfType<MemoNode>().Should().ContainSingle();
        union.Children.OfType<PlanRefNode>().Should().ContainSingle();
    }

    private sealed class ConstOp(bool value) : ICheckOp
    {
        public ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx,
            string entityType, string entityId, CancellationToken ct) => new(value);
        public string Describe() => $"Const({value})";
    }

    private sealed class AttributeHijackingRewriter : IPlanRewriter
    {
        public PlanNode Rewrite(PlanNode root, Schema schema)
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
