namespace Valtuutus.Core.Engines.Check.V2;

/// <summary>
/// Structural validation of a compiled+rewritten plan. Enforces the public rewriter
/// contract: memo slot ids in range, one MemoNode instance per slot (a rewriter that
/// rebuilt a shared memo into two instances silently degraded the DAG to a tree),
/// root-only nodes (<see cref="DirectRelationNode"/>, <see cref="AttributeTruthNode"/>)
/// never at interior positions, physical nodes carry an op. It exists to catch rewriter
/// bugs during development, not to police plans in production: the plan cache calls it
/// in debug builds only. Unknown PlanNode subtypes are not descended into — a custom
/// wrapper node's subtree is invisible to validation.
/// </summary>
public static class PlanValidator
{
    /// <summary>Walks the plan and throws on the first structural-contract violation.</summary>
    /// <param name="root">The rewritten plan root.</param>
    /// <param name="slotCount">Slot count from plan compilation; every <see cref="MemoNode.SlotId"/> must be in [0, slotCount).</param>
    /// <exception cref="InvalidOperationException">The plan violates a structural rule.</exception>
    public static void Validate(PlanNode root, int slotCount)
    {
        var roots = new HashSet<PlanNode>(ReferenceEqualityComparer.Instance) { root };
        Walk(root, slotCount, new HashSet<int>(), new HashSet<PlanNode>(ReferenceEqualityComparer.Instance), roots);
    }

    /// <summary>
    /// Same rules as <see cref="Validate"/>, applied to N roots sharing ONE slot space
    /// (<see cref="CombinedPlanCache"/>'s output) — a slot may legitimately appear as the same
    /// MemoNode instance under more than one of the N roots; two roots independently being
    /// root-only-legal shapes (e.g. one a DirectRelationNode, another a Union) is expected, not
    /// an error.
    /// </summary>
    public static void ValidateCombined(PlanNode[] roots, int slotCount)
    {
        var rootSet = new HashSet<PlanNode>(roots, ReferenceEqualityComparer.Instance);
        var seenSlots = new HashSet<int>();
        var visited = new HashSet<PlanNode>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
            Walk(root, slotCount, seenSlots, visited, rootSet);
    }

    private static void Walk(PlanNode node, int slotCount, HashSet<int> seenSlots, HashSet<PlanNode> visited,
        HashSet<PlanNode> roots)
    {
        if (!visited.Add(node)) return; // shared subtree — already checked
        switch (node)
        {
            case MemoNode m:
                if (m.SlotId < 0 || m.SlotId >= slotCount)
                    throw new InvalidOperationException(
                        $"Plan validation: memo slot {m.SlotId} out of range (slotCount {slotCount}).");
                // A re-encountered *instance* already returned early via the visited set,
                // so a seen slot here can only mean a second, distinct MemoNode.
                if (!seenSlots.Add(m.SlotId))
                    throw new InvalidOperationException(
                        $"Plan validation: two distinct MemoNode instances share slot {m.SlotId} — " +
                        "a rewriter rebuilt a shared memo without preserving reference identity.");
                Walk(m.Child, slotCount, seenSlots, visited, roots);
                break;
            case PhysicalCheckNode p:
                if (p.Op is null)
                    throw new InvalidOperationException(
                        "Plan validation: a rewriter constructed a PhysicalCheckNode without an ICheckOp; " +
                        "the op must be supplied at construction.");
                break;
            case DirectRelationNode when !roots.Contains(node):
                throw new InvalidOperationException(
                    "Plan validation: DirectRelationNode is root-only (a bare relation compiled as its " +
                    "own plan) and must not appear at an interior position.");
            case AttributeTruthNode when !roots.Contains(node):
                throw new InvalidOperationException(
                    "Plan validation: AttributeTruthNode is root-only (a bare attribute compiled as its " +
                    "own plan) and must not appear at an interior position.");
            case UnionNode u: foreach (var c in u.Children) Walk(c, slotCount, seenSlots, visited, roots); break;
            case IntersectNode i: foreach (var c in i.Children) Walk(c, slotCount, seenSlots, visited, roots); break;
            case NegateNode n: Walk(n.Child, slotCount, seenSlots, visited, roots); break;
        }
    }
}
