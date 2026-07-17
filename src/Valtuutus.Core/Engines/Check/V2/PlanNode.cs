using System.Collections.Immutable;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal abstract record PlanNode;

internal sealed record ConstNode(bool Value) : PlanNode
{
    public static readonly ConstNode False = new(false);
    public static readonly ConstNode True = new(true);
}

// Plan ROOTS only (a bare relation/attribute name compiled as its own plan).
internal sealed record DirectRelationNode(string Relation, bool HasSubRelationPaths) : PlanNode;
internal sealed record AttributeTruthNode(string Attribute) : PlanNode;

// In-tree leaves.
internal sealed record AttributeExprNode(PermissionNodeLeafExp Expr) : PlanNode;
internal sealed record TupleToUserSetNode(string TuplesetRelation, string ComputedRelation) : PlanNode;
internal sealed record PlanRefNode(string Permission) : PlanNode; // same-entity ResolveDynamic re-entry

internal sealed record UnionNode(ImmutableArray<PlanNode> Children) : PlanNode;
internal sealed record IntersectNode(ImmutableArray<PlanNode> Children) : PlanNode;
internal sealed record NegateNode(PlanNode Child) : PlanNode;
internal sealed record MemoNode(int SlotId, PlanNode Child) : PlanNode;

// ImmutableArray<T> makes generated record equality reference-based; hash-consing
// needs structural identity, so it goes through this comparer instead.
internal sealed class PlanNodeStructuralComparer : IEqualityComparer<PlanNode>
{
    public static readonly PlanNodeStructuralComparer Instance = new();

    public bool Equals(PlanNode? x, PlanNode? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.GetType() != y.GetType()) return false;
        return (x, y) switch
        {
            (UnionNode a, UnionNode b) => ChildrenEqual(a.Children, b.Children),
            (IntersectNode a, IntersectNode b) => ChildrenEqual(a.Children, b.Children),
            (NegateNode a, NegateNode b) => Equals(a.Child, b.Child),
            (MemoNode a, MemoNode b) => a.SlotId == b.SlotId && Equals(a.Child, b.Child),
            _ => x.Equals(y) // leaf records: generated value equality is correct
        };
    }

    private bool ChildrenEqual(ImmutableArray<PlanNode> a, ImmutableArray<PlanNode> b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }

    public int GetHashCode(PlanNode node)
    {
        switch (node)
        {
            case UnionNode u: return HashChildren(1, u.Children);
            case IntersectNode n: return HashChildren(2, n.Children);
            case NegateNode n: return HashCode.Combine(3, GetHashCode(n.Child));
            case MemoNode m: return HashCode.Combine(4, m.SlotId, GetHashCode(m.Child));
            default: return node.GetHashCode();
        }
    }

    private int HashChildren(int seed, ImmutableArray<PlanNode> children)
    {
        var h = new HashCode();
        h.Add(seed);
        foreach (var c in children) h.Add(GetHashCode(c));
        return h.ToHashCode();
    }
}
