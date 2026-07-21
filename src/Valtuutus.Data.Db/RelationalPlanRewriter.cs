using System.Collections.Immutable;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.Db;

// The Data.Db half of the rewrite seam: pattern recognition over the compiled plan lives here,
// ONCE, so relational providers only implement the op catalog (IRelationalCheckOps) and never
// touch trees. Core's compiler knows nothing about sibling fusion — recognizing that several
// sibling questions can be answered by one round trip is a relational concern, recognized here.
//
// Rule 1 (plan-time form of V1's runtime check-then-batch, CheckEngine.cs:559-577): >= 2 sibling
// Union/Intersect children that are plain same-entity direct-relation refs collapse into one
// HasAnyOfDirectRelationsOp — a single round trip. Only fires when the subject type is known
// (V1 parity: CheckEngine.cs:564; plans are keyed per subjectType, so that runtime condition is
// a rewrite-time one here). V1's memo exclusion has no plan-time equivalent — the fused batch
// may re-fetch a relation the dynamic memo already knows, which costs no extra round trip;
// deliberate divergence.
//
// Rule 2 (R4): symmetric for >= 2 sibling same-entity bool-attribute refs —
// HasAnyOfAttributesOp. No subjectType gate: attributes don't depend on subject at all.
//
// Both rules run over the post-hash-consing plan: a shared ref is MemoNode-wrapped by then and
// fails the PlanRefNode type test — that non-match IS the fusion barrier (design doc, "MemoNode
// barrier rule"). Contract per IPlanRewriter: unrecognized nodes pass through unchanged (and
// untouched subtrees come back reference-identical, preserving interning), never unwrap a
// MemoNode, stateless — one singleton serves every plan compile; per-Rewrite state lives in a
// Walker instance.
internal sealed class RelationalPlanRewriter : IPlanRewriter
{
    public PlanNode Rewrite(PlanNode root, Schema schema, string entityType, string? subjectType)
        => new Walker(schema, entityType, subjectType).Walk(root);

    private sealed class Walker(Schema schema, string entityType, string? subjectType)
    {
        // Ref-equality memo so a MemoNode shared by several parents rewrites to ONE instance,
        // preserving the DAG interchange form instead of silently duplicating shared subtrees.
        private readonly Dictionary<PlanNode, PlanNode> _visited = new(ReferenceEqualityComparer.Instance);

        public PlanNode Walk(PlanNode node)
        {
            if (_visited.TryGetValue(node, out var done)) return done;
            PlanNode result;
            switch (node)
            {
                case UnionNode u: result = GroupChildren(u, u.Children, isUnion: true); break;
                case IntersectNode i: result = GroupChildren(i, i.Children, isUnion: false); break;
                case NegateNode n:
                {
                    var child = Walk(n.Child);
                    result = ReferenceEquals(child, n.Child) ? n : new NegateNode(child);
                    break;
                }
                case MemoNode m:
                {
                    var child = Walk(m.Child);
                    result = ReferenceEquals(child, m.Child) ? m : new MemoNode(m.SlotId, child);
                    break;
                }
                default: result = node; break;
            }
            _visited[node] = result;
            return result;
        }

        private PlanNode GroupChildren(PlanNode original, ImmutableArray<PlanNode> children, bool isUnion)
        {
            // A ref can't be both (GetRelationType resolves a name to exactly one kind), so the
            // two collections are disjoint by construction.
            var directGate = !string.IsNullOrEmpty(subjectType);
            List<string>? relations = null;
            List<string>? attributes = null;
            foreach (var child in children)
            {
                if (directGate && IsBatchableDirectRef(child, out var relation))
                    (relations ??= []).Add(relation);
                else if (IsBatchableAttributeRef(child, out var attribute))
                    (attributes ??= []).Add(attribute);
            }

            // A group only fires with >= 2 members — one batchable child alone saves nothing.
            var fuseRelations = relations is { Count: >= 2 };
            var fuseAttributes = attributes is { Count: >= 2 };
            if (!fuseRelations && !fuseAttributes)
                return RebuildIfChanged(original, children, isUnion);

            var kept = ImmutableArray.CreateBuilder<PlanNode>(children.Length
                - (fuseRelations ? relations!.Count - 1 : 0)
                - (fuseAttributes ? attributes!.Count - 1 : 0));
            // Deterministic rewritten-plan shape — this ordering is contract, relied on by
            // tests and any plan snapshot: fused attribute group first, fused direct-relation
            // group second, then the unfused children in their original order. (It also matches
            // the shape the old sequential compiler passes produced: directs grouped first,
            // the attribute pass then prepended its node.)
            if (fuseAttributes)
                kept.Add(new PhysicalCheckNode(new HasAnyOfAttributesOp(attributes!.ToArray(), requireAll: !isUnion)));
            if (fuseRelations)
                kept.Add(new PhysicalCheckNode(new HasAnyOfDirectRelationsOp(relations!.ToArray(), requireAll: !isUnion)));
            foreach (var child in children)
            {
                if (fuseRelations && IsBatchableDirectRef(child, out _)) continue;
                if (fuseAttributes && IsBatchableAttributeRef(child, out _)) continue;
                kept.Add(Walk(child));
            }
            if (kept.Count == 1) return kept[0]; // single-survivor collapse: everything fused
            var keptArr = kept.MoveToImmutable();
            return isUnion ? new UnionNode(keptArr) : new IntersectNode(keptArr);
        }

        private PlanNode RebuildIfChanged(PlanNode original, ImmutableArray<PlanNode> children, bool isUnion)
        {
            var builder = ImmutableArray.CreateBuilder<PlanNode>(children.Length);
            var changed = false;
            foreach (var child in children)
            {
                var walked = Walk(child);
                changed |= !ReferenceEquals(walked, child);
                builder.Add(walked);
            }
            if (!changed) return original;
            var arr = builder.MoveToImmutable();
            return isUnion ? new UnionNode(arr) : new IntersectNode(arr);
        }

        // Mirror of V1 IsBatchableDirectRelation (CheckEngine.cs:411-423) over the compiled IR.
        private bool IsBatchableDirectRef(PlanNode node, out string relation)
        {
            relation = "";
            if (node is not PlanRefNode p) return false;
            if (schema.GetRelationType(entityType, p.Permission) != RelationType.DirectRelation) return false;
            if (schema.GetRelation(entityType, p.Permission).HasSubRelationPaths) return false;
            relation = p.Permission;
            return true;
        }

        // Mirror of IsBatchableDirectRef, checking Attribute instead of DirectRelation.
        private bool IsBatchableAttributeRef(PlanNode node, out string attribute)
        {
            attribute = "";
            if (node is not PlanRefNode p) return false;
            if (schema.GetRelationType(entityType, p.Permission) != RelationType.Attribute) return false;
            attribute = p.Permission;
            return true;
        }
    }
}
