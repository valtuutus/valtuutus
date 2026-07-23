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
// Two fusion passes run here, both over the post-hash-consing plan:
//
// Direct-relation sibling fusion (plan-time form of V1's runtime check-then-batch,
// CheckEngine.cs:559-577): >= 2 sibling Union/Intersect children that are plain same-entity
// direct-relation refs collapse into one HasAnyOfDirectRelationsOp — a single round trip. Only
// fires when the subject type is known (V1 parity: CheckEngine.cs:564; plans are keyed per
// subjectType, so that runtime condition is a rewrite-time one here). V1's memo exclusion has
// no plan-time equivalent — the fused batch may re-fetch a relation the dynamic memo already
// knows, which costs no extra round trip; deliberate divergence.
//
// Attribute sibling fusion: symmetric for >= 2 sibling same-entity bool-attribute refs —
// HasAnyOfAttributesOp. No subjectType gate: attributes don't depend on subject at all.
//
// Userset 2-hop join: a DirectRelationNode whose PlanCompiler.PruneAndFold pass already
// proved eligible (FastPathSubEntityType/FastPathComputedRelation both set) becomes one
// PhysicalCheckNode(UsersetJoinOp) — a single round trip replacing the runtime
// HasDirectRelation-then-GetIndirectRelations-fan-out sequence. Unlike the two fusion passes
// above, this recognizes a plan ROOT directly in Walk, not a Union/Intersect child in
// GroupChildren — DirectRelationNode never appears nested (PlanValidator enforces this), and
// IsBatchableDirectRef above already excludes HasSubRelationPaths relations, so the two passes
// never compete for the same node.
//
// Both passes rely on hash-consing having already run: a shared ref is MemoNode-wrapped by
// then and fails the PlanRefNode type test below — that non-match IS the fusion barrier (a
// MemoNode's child is shared by multiple parents, so fusing it into one duplicates the work
// for the others). Contract per IPlanRewriter: unrecognized nodes pass through unchanged (and
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

        // subjectType is fixed for a Walker's whole lifetime (one Walker per Rewrite call), so
        // this gate is computed once and shared by GroupChildren and TryRecognizeSingleLeaf
        // rather than recomputed as a per-call local.
        private readonly bool directGate = !string.IsNullOrEmpty(subjectType);

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
                case DirectRelationNode { FastPathSubEntityType: not null } d:
                    result = new PhysicalCheckNode(
                        new UsersetJoinOp(d.Relation, d.FastPathSubEntityType, d.FastPathComputedRelation!));
                    break;
                default: result = node; break;
            }
            _visited[node] = result;
            return result;
        }

        private PlanNode GroupChildren(PlanNode original, ImmutableArray<PlanNode> children, bool isUnion)
        {
            // A ref can't be both (GetRelationType resolves a name to exactly one kind), so the
            // two collections are disjoint by construction. Every child is walked FIRST (not the
            // raw child) before classification: a nested Union/Intersect child (e.g. the
            // "(owner or member)" in "org.admin and (owner or member)") only becomes recognizable
            // once its own sibling-group fusion has already turned it into a
            // PhysicalCheckNode(HasAnyOfDirectRelationsOp) — that happens inside Walk, so
            // classification must happen after. Walk is memoized (_visited), so walking every
            // child up front costs nothing extra when the partial-fusion path below walks
            // survivors again.
            List<string>? relations = null;
            List<string>? attributes = null;
            List<FusedCheckLeaf>? singleLeaves = null;
            var fullyRecognized = true;
            foreach (var child in children)
            {
                var walked = Walk(child);
                if (directGate && IsBatchableDirectRef(walked, out var relation))
                    (relations ??= []).Add(relation);
                else if (IsBatchableAttributeRef(walked, out var attribute))
                    (attributes ??= []).Add(attribute);
                // A nested FusedExpressionOp holds its own ImmutableArray<FusedCheckLeaf> rather
                // than a single Names/RequireAll pair, so it can't go through
                // TryRecognizeSingleLeaf's single-leaf-out contract — it contributes MANY leaves,
                // handled here directly. Flattening is only safe when the nested op's own
                // combinator agrees with what this outer level needs (f.RequireAll == !isUnion):
                // an inner OR's leaves flattened into an outer AND (or vice versa) would silently
                // change the boolean semantics. On a mismatch this child is left unrecognized —
                // it survives as an ordinary sibling via the partial-fusion path below, still a
                // single round trip for itself, just not folded into the outer one.
                else if (walked is PhysicalCheckNode { Op: FusedExpressionOp f } && f.RequireAll == !isUnion)
                    (singleLeaves ??= []).AddRange(f.Leaves);
                else if (TryRecognizeSingleLeaf(walked, out var leaf))
                    (singleLeaves ??= []).Add(leaf);
                else
                    fullyRecognized = false;
            }

            // Full fusion: every child accounted for, and collapsing groups still leaves >= 2
            // distinct leaves — a single already-optimal group (e.g. 3 plain direct-relation
            // siblings, nothing else) is left to the single-group path below unchanged; wrapping
            // it in FusedExpressionOp would gain nothing and would silently change its SQL text.
            if (fullyRecognized)
            {
                var fused = ImmutableArray.CreateBuilder<FusedCheckLeaf>();
                if (attributes is { Count: >= 2 })
                    fused.Add(new FusedCheckLeaf(FusedLeafKind.MultiAttribute, false, attributes.ToArray(), !isUnion));
                else if (attributes is { Count: 1 })
                    fused.Add(new FusedCheckLeaf(FusedLeafKind.Attribute, false, [attributes[0]]));
                if (relations is { Count: >= 2 })
                    fused.Add(new FusedCheckLeaf(FusedLeafKind.MultiDirect, false, relations.ToArray(), !isUnion));
                else if (relations is { Count: 1 })
                    fused.Add(new FusedCheckLeaf(FusedLeafKind.Direct, false, [relations[0]]));
                if (singleLeaves is not null) fused.AddRange(singleLeaves);

                if (fused.Count >= 2)
                    return new PhysicalCheckNode(new FusedExpressionOp(fused.ToImmutable(), requireAll: !isUnion));
            }

            // Partial fusion (unchanged pre-existing behavior): only the direct-relation and/or
            // attribute sibling groups fuse (each needs >= 2 members — one batchable child alone
            // saves nothing); everything else, including TTU/negated/nested leaves recognized
            // above, survives as its own walked child. Note this rebuild loop still checks
            // IsBatchableDirectRef/IsBatchableAttributeRef against the RAW child (not `walked`) —
            // identical result for the cases that matter here (a raw direct/attribute PlanRefNode
            // walks to itself), and Walk(child) below is a memoized re-fetch of what the
            // classification loop above already computed, not new work.
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

        // Recognizes a single (non-grouped) leaf the full-fusion path above can use directly: a
        // plan-time TTU fast-path node (eligibility already proved by PlanCompiler.PruneAndFold),
        // an already-fused HasAnyOfDirectRelationsOp/HasAnyOfAttributesOp sibling group nested one
        // level down (walked before this is called, so a former Union/Intersect child arrives
        // here as a PhysicalCheckNode if its own children partially fused), or a Negate over any
        // of those / a direct-relation ref / an attribute ref. Non-negated direct-relation/
        // attribute refs are handled by IsBatchableDirectRef/IsBatchableAttributeRef above (they
        // feed the sibling-group lists, not this method) — this method only covers what those two
        // do not. A nested PhysicalCheckNode { Op: FusedExpressionOp } is NOT handled here:
        // it needs to contribute MULTIPLE leaves to the outer fusion (not one opaque leaf), which
        // this method's single-leaf-out signature can't express — GroupChildren's classification
        // loop checks for and flattens that case itself, before ever calling this method.
        private bool TryRecognizeSingleLeaf(PlanNode node, out FusedCheckLeaf leaf)
        {
            switch (node)
            {
                case TupleToUserSetNode { FastPathSubEntityType: not null } t:
                    leaf = new FusedCheckLeaf(FusedLeafKind.TupleToUserSet, false,
                        [t.TuplesetRelation], TtuSubEntityType: t.FastPathSubEntityType, TtuComputedRelation: t.ComputedRelation);
                    return true;
                case PhysicalCheckNode { Op: HasAnyOfDirectRelationsOp d }:
                    leaf = new FusedCheckLeaf(FusedLeafKind.MultiDirect, false, d.Relations, d.RequireAll);
                    return true;
                case PhysicalCheckNode { Op: HasAnyOfAttributesOp a }:
                    leaf = new FusedCheckLeaf(FusedLeafKind.MultiAttribute, false, a.Attributes, a.RequireAll);
                    return true;
                case NegateNode { Child: PlanRefNode p } when directGate
                        && schema.GetRelationType(entityType, p.Permission) == RelationType.DirectRelation
                        && !schema.GetRelation(entityType, p.Permission).HasSubRelationPaths:
                    leaf = new FusedCheckLeaf(FusedLeafKind.Direct, true, [p.Permission]);
                    return true;
                case NegateNode { Child: PlanRefNode p2 } when schema.GetRelationType(entityType, p2.Permission) == RelationType.Attribute:
                    leaf = new FusedCheckLeaf(FusedLeafKind.Attribute, true, [p2.Permission]);
                    return true;
                case NegateNode { Child: TupleToUserSetNode { FastPathSubEntityType: not null } t2 }:
                    leaf = new FusedCheckLeaf(FusedLeafKind.TupleToUserSet, true,
                        [t2.TuplesetRelation], TtuSubEntityType: t2.FastPathSubEntityType, TtuComputedRelation: t2.ComputedRelation);
                    return true;
                case NegateNode { Child: PhysicalCheckNode { Op: HasAnyOfDirectRelationsOp d2 } }:
                    leaf = new FusedCheckLeaf(FusedLeafKind.MultiDirect, true, d2.Relations, d2.RequireAll);
                    return true;
                case NegateNode { Child: PhysicalCheckNode { Op: HasAnyOfAttributesOp a2 } }:
                    leaf = new FusedCheckLeaf(FusedLeafKind.MultiAttribute, true, a2.Attributes, a2.RequireAll);
                    return true;
                default:
                    leaf = null!;
                    return false;
            }
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
