using System.Collections.Immutable;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal static class PlanCompiler
{
    public static CheckPlan Compile(Schema schema, string entityType, string permission, string? subjectType)
    {
        // Top-level guard, mirrors CheckInternal's CanSubjectTypeReach check.
        if (subjectType is not null && !schema.CanSubjectTypeReach(entityType, permission, subjectType))
            return new CheckPlan(ConstNode.False, SlotCount: 0);

        var root = CompileRoot(schema, entityType, permission);
        root = PruneAndFold(root, schema, entityType, subjectType);
        var (consed, slotCount) = HashCons(root);
        // V1 parity: batching only fires when the subject type is known (CheckEngine.cs:564);
        // plans are keyed per subjectType, so that runtime condition is a compile-time one here.
        if (!string.IsNullOrEmpty(subjectType))
            consed = GroupSiblingDirectRelations(consed, schema, entityType);
        return new CheckPlan(consed, slotCount);
    }

    // Plan-time form of V1's runtime check-then-batch (CheckEngine.cs:559-577): ≥2 sibling
    // Union/Intersect children that are plain same-entity direct-relation refs collapse into one
    // MultiDirectNode, answered by a single HasAnyOfDirectRelations round trip. V1's memo
    // exclusion has no plan-time equivalent — the fused batch may re-fetch a relation the
    // dynamic memo already knows, which costs no extra round trip; deliberate divergence.
    // Runs post-hash-consing: a shared ref is MemoNode-wrapped by then and fails the PlanRefNode
    // type test below — that non-match IS the fusion barrier (design doc, "MemoNode barrier rule").
    private static PlanNode GroupSiblingDirectRelations(PlanNode root, Schema schema, string entityType)
    {
        // Ref-equality memo so a MemoNode shared by several parents rewrites to ONE instance,
        // preserving the DAG interchange form instead of silently duplicating shared subtrees.
        var visited = new Dictionary<PlanNode, PlanNode>(ReferenceEqualityComparer.Instance);
        return Walk(root);

        PlanNode Walk(PlanNode node)
        {
            if (visited.TryGetValue(node, out var done)) return done;
            PlanNode result;
            switch (node)
            {
                case UnionNode u: result = GroupChildren(u.Children, isUnion: true); break;
                case IntersectNode i: result = GroupChildren(i.Children, isUnion: false); break;
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
            visited[node] = result;
            return result;
        }

        PlanNode GroupChildren(ImmutableArray<PlanNode> children, bool isUnion)
        {
            List<string>? batchable = null;
            foreach (var child in children)
                if (IsBatchableDirectRef(child, out var relation))
                    (batchable ??= []).Add(relation);

            if (batchable is not { Count: >= 2 })
            {
                var rebuilt = ImmutableArray.CreateBuilder<PlanNode>(children.Length);
                foreach (var child in children) rebuilt.Add(Walk(child));
                var arr = rebuilt.MoveToImmutable();
                return isUnion ? new UnionNode(arr) : new IntersectNode(arr);
            }

            var multi = new MultiDirectNode(batchable.ToArray(), RequireAll: !isUnion);
            var kept = ImmutableArray.CreateBuilder<PlanNode>(children.Length - batchable.Count + 1);
            kept.Add(multi);
            foreach (var child in children)
                if (!IsBatchableDirectRef(child, out _))
                    kept.Add(Walk(child));
            if (kept.Count == 1) return multi;
            var keptArr = kept.MoveToImmutable();
            return isUnion ? new UnionNode(keptArr) : new IntersectNode(keptArr);
        }

        // Mirror of V1 IsBatchableDirectRelation (CheckEngine.cs:411-423) over the compiled IR.
        bool IsBatchableDirectRef(PlanNode node, out string relation)
        {
            relation = "";
            if (node is not PlanRefNode p) return false;
            if (schema.GetRelationType(entityType, p.Permission) != RelationType.DirectRelation) return false;
            if (schema.GetRelation(entityType, p.Permission).HasSubRelationPaths) return false;
            relation = p.Permission;
            return true;
        }
    }

    // Bottom-up interning: identical subtrees become one node; any node referenced more than
    // once gets a MemoNode slot. MemoNode is also the rewrite barrier for later provider
    // passes — never fuse across it (design doc, "MemoNode barrier rule").
    private static (PlanNode Root, int SlotCount) HashCons(PlanNode root)
    {
        var interned = new Dictionary<PlanNode, PlanNode>(PlanNodeStructuralComparer.Instance);
        var refCounts = new Dictionary<PlanNode, int>(ReferenceEqualityComparer.Instance);

        PlanNode Intern(PlanNode node)
        {
            var canonical = node switch
            {
                UnionNode u => InternExpr(u, static c => new UnionNode(c), u.Children),
                IntersectNode i => InternExpr(i, static c => new IntersectNode(c), i.Children),
                NegateNode n => GetOrAdd(new NegateNode(Intern(n.Child))),
                _ => GetOrAdd(node),
            };
            refCounts[canonical] = refCounts.TryGetValue(canonical, out var c2) ? c2 + 1 : 1;
            return canonical;
        }

        PlanNode InternExpr(PlanNode original, Func<ImmutableArray<PlanNode>, PlanNode> rebuild,
            ImmutableArray<PlanNode> children)
        {
            var builder = ImmutableArray.CreateBuilder<PlanNode>(children.Length);
            foreach (var child in children) builder.Add(Intern(child));
            return GetOrAdd(rebuild(builder.MoveToImmutable()));
        }

        PlanNode GetOrAdd(PlanNode node)
        {
            if (interned.TryGetValue(node, out var existing)) return existing;
            interned[node] = node;
            return node;
        }

        var canonicalRoot = Intern(root);

        // Assign slots to shared non-Const nodes, rebuild with MemoNode wrappers.
        var slots = new Dictionary<PlanNode, int>(ReferenceEqualityComparer.Instance);
        foreach (var (node, count) in refCounts)
            if (count > 1 && node is not ConstNode)
                slots[node] = slots.Count;

        if (slots.Count == 0) return (canonicalRoot, 0);

        var memoized = new Dictionary<PlanNode, MemoNode>(ReferenceEqualityComparer.Instance);
        PlanNode Wrap(PlanNode node)
        {
            var rebuilt = node switch
            {
                UnionNode u => new UnionNode([.. u.Children.Select(Wrap)]),
                IntersectNode i => (PlanNode)new IntersectNode([.. i.Children.Select(Wrap)]),
                NegateNode n => new NegateNode(Wrap(n.Child)),
                _ => node,
            };
            if (!slots.TryGetValue(node, out var slot)) return rebuilt;
            if (memoized.TryGetValue(node, out var memo)) return memo;
            memo = new MemoNode(slot, rebuilt);
            memoized[node] = memo;
            return memo;
        }

        return (Wrap(canonicalRoot), slots.Count);
    }

    private static PlanNode PruneAndFold(PlanNode node, Schema schema, string entityType, string? subjectType)
    {
        switch (node)
        {
            case PlanRefNode r when subjectType is not null
                    && !schema.CanSubjectTypeReach(entityType, r.Permission, subjectType):
                return ConstNode.False;

            case NegateNode n:
                var inner = PruneAndFold(n.Child, schema, entityType, subjectType);
                return inner is ConstNode c ? (c.Value ? ConstNode.False : ConstNode.True) : new NegateNode(inner);

            case UnionNode u:
                return FoldChildren(u.Children, schema, entityType, subjectType, isUnion: true);

            case IntersectNode i:
                return FoldChildren(i.Children, schema, entityType, subjectType, isUnion: false);

            default:
                return node;
        }
    }

    private static PlanNode FoldChildren(ImmutableArray<PlanNode> children, Schema schema,
        string entityType, string? subjectType, bool isUnion)
    {
        var kept = ImmutableArray.CreateBuilder<PlanNode>(children.Length);
        foreach (var child in children)
        {
            var folded = PruneAndFold(child, schema, entityType, subjectType);
            if (folded is ConstNode c)
            {
                if (c.Value == isUnion) return c;   // absorbing element decides the node
                continue;                            // identity element drops out
            }
            kept.Add(folded);
        }
        if (kept.Count == 0) return isUnion ? ConstNode.False : ConstNode.True; // V1: `return !isUnion`
        if (kept.Count == 1) return kept[0];
        return isUnion ? new UnionNode(kept.ToImmutable()) : new IntersectNode(kept.ToImmutable());
    }

    private static PlanNode CompileRoot(Schema schema, string entityType, string permission) =>
        schema.GetRelationType(entityType, permission) switch
        {
            RelationType.DirectRelation => new DirectRelationNode(permission,
                schema.GetRelation(entityType, permission).HasSubRelationPaths),
            RelationType.Attribute => new AttributeTruthNode(permission),
            RelationType.Permission => CompileTree(schema.GetPermission(entityType, permission).Tree),
            _ => ConstNode.False
        };

    private static PlanNode CompileTree(PermissionNode node)
    {
        if (node.Type == PermissionNodeType.Expression)
        {
            var expr = node.ExpressionNode!;
            if (expr.Operation == PermissionOperation.Negate)
                return new NegateNode(CompileTree(expr.Children[0]));

            var children = ImmutableArray.CreateBuilder<PlanNode>(expr.Children.Count);
            foreach (var child in expr.Children)
                children.Add(CompileTree(child));
            return expr.Operation == PermissionOperation.Union
                ? new UnionNode(children.MoveToImmutable())
                : new IntersectNode(children.MoveToImmutable());
        }

        var leaf = node.LeafNode!;
        if (leaf.Type == PermissionNodeLeafType.Expression)
            return new AttributeExprNode(leaf.ExpressionNode!);

        var perm = leaf.PermissionNode!;
        return perm.IsIndirect
            ? new TupleToUserSetNode(perm.UserSet!, perm.ComputedUserSet!)
            : new PlanRefNode(perm.Permission);
    }
}
