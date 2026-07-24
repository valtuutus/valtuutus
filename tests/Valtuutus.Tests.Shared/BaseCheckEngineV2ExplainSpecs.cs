using FluentAssertions;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Tests.Shared;

// V2-only Explain facts: fusion (no V1 equivalent — V1 never rewrites its plan), shared-subtree
// dedup (no V1 equivalent — V1 has no hash-consed plan), and the skipped-sibling wording on a
// short-circuit. These do not belong on BaseCheckEngineExplainSpecs because they don't hold for
// V1 (it never fuses, and has no MemoNode-style shared-subtree concept).
public abstract class BaseCheckEngineV2ExplainSpecs : BaseCheckEngineExplainSpecs
{
    protected BaseCheckEngineV2ExplainSpecs(IDatabaseFixture fixture) : base(fixture) { }
    protected override bool UseCheckV2 => true;

    [Fact]
    public async Task Explain_MemoizedSubCheck_RecordsMemoizedSharedSubtreeDetail()
    {
        // edit := (owner and flag) or (owner and not(flag))
        // PlanCompiler.HashCons operates within ONE compiled plan: "owner" must appear twice in
        // edit's OWN tree to be interned into a shared MemoNode slot (unlike V1's cross-call memo,
        // which spans separate CheckInternal calls — e.g. a permission and a sub-permission it
        // references, each compiled as its own plan). The first "owner" reference actually
        // executes; the second hits the already-in-flight/-done slot and renders as a childless
        // leaf with this distinct detail.
        const string memoSchema = """
            entity user {}
            entity doc {
                relation owner @user;
                attribute flag bool;
                permission edit := (owner and flag) or (owner and not(flag));
            }
            """;
        var engine = await CreateEngine(
            [new RelationTuple("doc", "v2-expl-m1", "owner", "user", "alice")],
            [new AttributeTuple("doc", "v2-expl-m1", "flag", System.Text.Json.Nodes.JsonValue.Create(true))],
            memoSchema);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "doc", EntityId = "v2-expl-m1",
            Permission = "edit",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        var memoizedNode = FindNode(result.Root, n => n.Detail == "memoized (shared subtree)");
        memoizedNode.Should().NotBeNull("a shared subtree reference should be recorded distinctly from V1's cross-call memo");
        memoizedNode!.Children.Should().BeEmpty("a memoized shared-subtree reference is a leaf");
    }

    // NOTE: Explain_UnionExpression_SkippedSiblingHasWording deliberately does NOT live here.
    // This fact is genuinely deterministic on InMemory (the mailbox drains one completion at a
    // time, and only the TRUE child can ever trigger a Union's short-circuit — false siblings
    // just decrement Pending — so as long as InMemory's synchronous per-op completion means the
    // wave is processed in submission order, at least one sibling is guaranteed still-pending
    // when the decider completes) but a REAL RACE on Postgres/SqlServer: all N relation lookups
    // fire as genuinely concurrent network round-trips, and it's entirely possible for every
    // false sibling to complete (and get marked Completed) before the true one's completion is
    // even dequeued — leaving MarkSiblingsSkipped nothing to mark. Since this class is the shared
    // base for all providers, this fact instead lives directly on InMemory's own concrete test
    // class, where it's provably safe.
}
