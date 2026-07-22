using System.Collections.Immutable;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

/// <summary>
/// Root of the compiled check-plan IR. The IR mirrors the schema language (relations,
/// attributes, unions/intersections/negations); new node types are additive — rewriters
/// must return unrecognized nodes unchanged.
/// </summary>
public abstract record PlanNode;

public sealed record ConstNode(bool Value) : PlanNode
{
    public static readonly ConstNode False = new(false);
    public static readonly ConstNode True = new(true);
}

/// <summary>
/// Appears as a plan ROOT only (a bare relation name compiled as its own plan), never as an
/// interior node.
/// </summary>
public sealed record DirectRelationNode(string Relation, bool HasSubRelationPaths) : PlanNode;

/// <summary>
/// Appears as a plan ROOT only (a bare attribute name compiled as its own plan), never as an
/// interior node.
/// </summary>
public sealed record AttributeTruthNode(string Attribute) : PlanNode;

// In-tree leaves.
public sealed record AttributeExprNode(PermissionNodeLeafExp Expr) : PlanNode;
/// <summary>
/// <paramref name="FastPathSubEntityType"/> is non-null when plan-time analysis proved the
/// runtime fast-path guard (single non-userset tupleset target, computed relation a direct
/// relation with no sub-relation-paths, admits the plan key's subjectType) holds for this node
/// — set by <see cref="PlanCompiler"/>'s PruneAndFold pass, never by CompileTree. Null means
/// either the guard doesn't hold, or subjectType was unknown at compile time.
/// </summary>
public sealed record TupleToUserSetNode(
    string TuplesetRelation, string ComputedRelation, string? FastPathSubEntityType = null) : PlanNode;

/// <summary>
/// Re-entry into the same entity's compiled plan for the named permission, relation, or
/// attribute.
/// </summary>
public sealed record PlanRefNode(string Permission) : PlanNode; // executed via ResolveDynamic re-entry

/// <summary>
/// Escape hatch for provider-fused execution: a rewriter-produced node carrying its own
/// opaque op. Reference equality; created only after hash-consing, never interned.
/// </summary>
public sealed record PhysicalCheckNode(ICheckOp Op) : PlanNode;

public sealed record UnionNode(ImmutableArray<PlanNode> Children) : PlanNode;
public sealed record IntersectNode(ImmutableArray<PlanNode> Children) : PlanNode;
public sealed record NegateNode(PlanNode Child) : PlanNode;

/// <summary>
/// Rewrite barrier: never unwrap a MemoNode — its child is shared by multiple parents,
/// and a shared subtree fused into one parent duplicates work for the others.
/// </summary>
public sealed record MemoNode(int SlotId, PlanNode Child) : PlanNode;

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
