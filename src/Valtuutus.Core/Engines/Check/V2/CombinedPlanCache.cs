using System.Collections.Concurrent;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal sealed record CombinedPlan(PlanNode[] Roots, int SlotCount);

// Registered as a DI singleton alongside CheckPlanCache/Schema — same lifetime reasoning: a
// schema reload produces a new container, so a fresh cache comes for free without an explicit
// version field. Used only by CheckEngineV2.SubjectPermission; Check()/Explain() stay on
// CheckPlanCache exclusively and never construct or see a CombinedPlan. Keyed by
// (EntityType, SubjectType) only, never by permission set: SubjectPermissionRequest has no
// permission-subset parameter, so "all permissions for EntityType" is the only set any caller
// can request today. A future subset API should recompute the combined plan on demand from the
// already-cached per-permission trees instead of extending this cache to a per-subset key — a
// per-subset key risks 2^N cache growth as the requested set varies, where recomputation from
// small pre-compiled trees is cheap and bounded.
internal sealed class CombinedPlanCache(Schema schema, IEnumerable<IPlanRewriter>? rewriters = null)
{
    private readonly Schema _schema = schema;
    private readonly IPlanRewriter[] _rewriters = rewriters?.ToArray() ?? [];
    private readonly ConcurrentDictionary<CombinedPlanKey, CombinedPlan> _plans = new();

    public CombinedPlan GetOrCompile(string entityType, string? subjectType)
        => _plans.GetOrAdd(new CombinedPlanKey(entityType, subjectType),
            static (key, self) =>
            {
                var permissions = self._schema.GetPermissions(key.EntityType);
                var pruned = new PlanNode[permissions.Length];
                var i = 0;
                foreach (var perm in permissions)
                {
                    // Mirrors PlanCompiler.Compile's own top-level guard, applied per permission
                    // instead of once — a permission unreachable by subjectType still needs a
                    // ConstNode.False ROOT (not to be omitted), so combined.Roots stays index-
                    // aligned with schema.GetPermissions(entityType) for CheckEngineV2 to zip
                    // against its own names[] array.
                    pruned[i++] = key.SubjectType is not null
                        && !self._schema.CanSubjectTypeReach(key.EntityType, perm.Name, key.SubjectType)
                        ? ConstNode.False
                        : PlanCompiler.Prune(self._schema, key.EntityType, perm.Name, key.SubjectType);
                }

                var (consed, slotCount) = PlanCompiler.Cons(pruned);

                // Rewriters run once PER ROOT, unchanged — IPlanRewriter.Rewrite already takes
                // one root and is contractually forbidden from unwrapping a MemoNode, so N calls
                // over N roots sharing a MemoNode child cannot double-fuse or diverge (each call
                // stops at the MemoNode boundary by contract). Combine BEFORE rewriting so
                // structural-sharing detection isn't blinded by PhysicalCheckNode's deliberate
                // reference-only equality (a rewriter-fused node is never interned).
                for (var j = 0; j < consed.Length; j++)
                {
                    var root = consed[j];
                    foreach (var rewriter in self._rewriters)
                        root = rewriter.Rewrite(root, self._schema, key.EntityType, key.SubjectType);
                    consed[j] = root;
                }
#if DEBUG
                PlanValidator.ValidateCombined(consed, slotCount);
#endif
                return new CombinedPlan(consed, slotCount);
            },
            this);

    private readonly record struct CombinedPlanKey(string EntityType, string? SubjectType);
}
