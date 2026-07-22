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
        // Sibling fusion (batching several same-entity refs into one round trip) is NOT the
        // compiler's job: it lives in provider-side IPlanRewriter implementations
        // (Valtuutus.Data.Db's RelationalPlanRewriter), applied by CheckPlanCache after compile.
        return new CheckPlan(consed, slotCount);
    }

    // Bottom-up interning: identical subtrees become one node; any node referenced more than
    // once gets a MemoNode slot. MemoNode is also the rewrite barrier for later provider
    // passes — never fuse across it: the child is shared by multiple parents, and fusing it
    // into one duplicates the work for the others.
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

            case TupleToUserSetNode t:
                return PruneTupleToUserSet(t, schema, entityType, subjectType);

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

    // R3: StepTupleToUserSet's runtime fast-path guard (CheckPlanExecutor.cs:504-513, mirroring
    // CheckEngine.cs:742-753) is schema-static given (entityType, subjectType) apart from the
    // per-request recursion budget (frame.Depth > 0, which stays a runtime check — see the R3
    // plan's context notes for why). Decide the schema-static part once here instead of
    // re-deriving it on every request. Also folds statically-dead TTU branches to ConstNode.False,
    // the TTU analogue of the PlanRefNode prune case above.
    private static PlanNode PruneTupleToUserSet(TupleToUserSetNode t, Schema schema, string entityType,
        string? subjectType)
    {
        if (subjectType is null) return t; // fast path needs a known subjectType; nothing to decide yet

        var tuplesetRel = schema.GetRelation(entityType, t.TuplesetRelation);

        var reachable = false;
        foreach (var e in tuplesetRel.Entities)
        {
            if (schema.CanSubjectTypeReach(e.Type, t.ComputedRelation, subjectType)) { reachable = true; break; }
        }
        if (!reachable) return ConstNode.False;

        if (tuplesetRel.Entities.Count == 1 && tuplesetRel.Entities[0].Relation is null)
        {
            var subEntityType = tuplesetRel.Entities[0].Type;
            if (schema.GetRelationType(subEntityType, t.ComputedRelation) == RelationType.DirectRelation)
            {
                var computedRel = schema.GetRelation(subEntityType, t.ComputedRelation);
                if (!computedRel.HasSubRelationPaths && computedRel.EntityTypes.Contains(subjectType))
                    return t with { FastPathSubEntityType = subEntityType };
            }
        }

        return t;
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
