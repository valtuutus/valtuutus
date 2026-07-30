using FluentAssertions;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.InMemory.Tests.V2;

public class CombinedPlanCacheSpecs
{
    private static Schema Parse(string text) => PlanCompilerSpecs.Parse(text);

    private const string TeamSchema = """
        entity user {}
        entity organization {
            relation admin @user;
        }
        entity team {
            relation owner @user;
            relation org @organization;
            permission edit := org.admin or owner;
            permission delete := org.admin or owner;
        }
        """;

    [Fact]
    public void Combines_all_permissions_for_an_entity_type()
    {
        var cache = new CombinedPlanCache(Parse(TeamSchema));
        var combined = cache.GetOrCompile("team", "user");
        combined.Roots.Should().HaveCount(2); // edit, delete
    }

    [Fact]
    public void Identical_permission_bodies_share_one_combined_slot()
    {
        var cache = new CombinedPlanCache(Parse(TeamSchema));
        var combined = cache.GetOrCompile("team", "user");
        // 3, not 1: the outer Union AND both its leaves (org.admin, owner) are each referenced
        // by both edit and delete, so each independently crosses the refcount>1 threshold — see
        // the comment on PlanCompilerSpecs.Cons_of_two_roots_sharing_a_subtree_gets_one_combined_slot
        // for why this is correct (not minimal, but the nested slots are never independently
        // re-triggered since the outer MemoNode's memoization intercepts first). The load-bearing
        // assertion here is the second line: both permissions get the literal same root object.
        combined.SlotCount.Should().Be(3);
        combined.Roots[0].Should().BeSameAs(combined.Roots[1]);
    }

    [Fact]
    public void Cache_returns_same_instance_for_same_key()
    {
        var cache = new CombinedPlanCache(Parse(TeamSchema));
        var a = cache.GetOrCompile("team", "user");
        var b = cache.GetOrCompile("team", "user");
        b.Roots.Should().BeSameAs(a.Roots);
    }

    [Fact]
    public void Cache_keys_include_subjectType()
    {
        const string s = """
            entity user {}
            entity service_account {}
            entity organization {
                relation admin @user;
                relation bot @service_account;
                permission access := admin or bot;
            }
            """;
        var cache = new CombinedPlanCache(Parse(s));
        var forUser = cache.GetOrCompile("organization", "user");
        var forBot = cache.GetOrCompile("organization", "service_account");
        forUser.Roots.Should().NotBeSameAs(forBot.Roots);
    }

    [Fact]
    public void Permission_unreachable_by_subjectType_becomes_ConstFalse_not_omitted()
    {
        const string s = """
            entity user {}
            entity service_account {}
            entity organization {
                relation admin @user;
                relation bot @service_account;
                permission access := admin;
                permission botOnly := bot;
            }
            """;
        var cache = new CombinedPlanCache(Parse(s));
        var combined = cache.GetOrCompile("organization", "user");
        // Both permissions still get a root (index alignment with schema.GetPermissions is a
        // hard requirement for CheckEngineV2.SubjectPermission's names[]/roots[] pairing) —
        // botOnly's root just folds to ConstNode.False for a "user" subject.
        combined.Roots.Should().HaveCount(2);
        combined.Roots.Should().Contain(r => r == ConstNode.False);
    }

    [Fact]
    public void GetOrCompile_never_trips_ValidateCombineds_root_only_rule()
    {
        // Regression guard: PlanValidator.ValidateCombined's root-only check could in principle
        // false-positive on a bare DirectRelationNode/AttributeTruthNode wrapped in a MemoNode
        // across multiple combined roots. Traced and confirmed unreachable through CombinedPlanCache
        // specifically, because Schema.GetRelationType always classifies a name drawn from
        // schema.GetPermissions() as RelationType.Permission
        // (Permissions dictionary is checked first) — so PlanCompiler.Prune's CompileRoot call can
        // never take the DirectRelationNode/AttributeTruthNode branch for anything CombinedPlanCache
        // compiles, even when a permission's entire body is just a bare relation alias (bareAlias
        // below). This test pins that invariant so a future change can't silently reintroduce the
        // false-positive path. #if DEBUG is required because ValidateCombined only runs in DEBUG
        // builds inside GetOrCompile — this test needs to observe that path directly, so it calls
        // GetOrCompile normally (which already runs ValidateCombined internally in DEBUG) and just
        // asserts it doesn't throw; the meaningful assertion IS "doesn't throw" here.
        const string s = """
            entity user {}
            entity organization {
                relation admin @user;
            }
            entity team {
                relation owner @user;
                relation org @organization;
                permission edit := org.admin or owner;
                permission delete := org.admin or owner;
                permission bareAlias := owner;
            }
            """;
        var cache = new CombinedPlanCache(Parse(s));
        var act = () => cache.GetOrCompile("team", "user");
        act.Should().NotThrow();
    }

    [Fact]
    public void Entity_type_with_no_permissions_returns_empty_combined_plan()
    {
        const string s = """
            entity user {}
            entity doc {
                relation owner @user;
            }
            """;
        var cache = new CombinedPlanCache(Parse(s));
        var act = () => cache.GetOrCompile("doc", "user");
        act.Should().NotThrow();
        var combined = cache.GetOrCompile("doc", "user");
        combined.Roots.Should().BeEmpty();
        combined.SlotCount.Should().Be(0);
    }
}
