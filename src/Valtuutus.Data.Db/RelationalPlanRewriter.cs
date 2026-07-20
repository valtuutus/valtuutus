using System.Collections.Immutable;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.Db;

// The Data.Db half of the rewrite seam: pattern recognition over the compiled plan lives here,
// ONCE, so relational providers only implement the op catalog (IRelationalCheckOps) and never
// touch trees. Contract per IPlanRewriter: unrecognized nodes pass through unchanged (and
// untouched subtrees come back reference-identical, preserving interning), never unwrap a
// MemoNode (fusion barrier), stateless — one singleton serves every plan compile.
internal sealed class RelationalPlanRewriter : IPlanRewriter
{
    public PlanNode Rewrite(PlanNode root, Schema schema) => Walk(root);

    private static PlanNode Walk(PlanNode node)
    {
        switch (node)
        {
            case MultiDirectNode m:
                return new PhysicalCheckNode(new HasAnyOfDirectRelationsOp(m.Relations, m.RequireAll));
            case MultiAttributeNode m:
                return new PhysicalCheckNode(new HasAnyOfAttributesOp(m.Attributes, m.RequireAll));
            case UnionNode u:
                return RebuildIfChanged(u.Children, static c => new UnionNode(c), u);
            case IntersectNode i:
                return RebuildIfChanged(i.Children, static c => new IntersectNode(c), i);
            case NegateNode n:
            {
                var child = Walk(n.Child);
                return ReferenceEquals(child, n.Child) ? n : new NegateNode(child);
            }
            case MemoNode m:
            {
                var child = Walk(m.Child);
                return ReferenceEquals(child, m.Child) ? m : new MemoNode(m.SlotId, child);
            }
            default:
                return node;
        }
    }

    private static PlanNode RebuildIfChanged(ImmutableArray<PlanNode> children,
        Func<ImmutableArray<PlanNode>, PlanNode> rebuild, PlanNode original)
    {
        var builder = ImmutableArray.CreateBuilder<PlanNode>(children.Length);
        var changed = false;
        foreach (var child in children)
        {
            var walked = Walk(child);
            changed |= !ReferenceEquals(walked, child);
            builder.Add(walked);
        }
        return changed ? rebuild(builder.MoveToImmutable()) : original;
    }
}
