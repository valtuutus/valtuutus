namespace Valtuutus.Data.Db;

/// <summary>
/// One relational-leaf shape a fused boolean expression (see <c>IRelationalCheckOps.HasFusedExpression</c>)
/// can compose. Built exclusively by <c>RelationalPlanRewriter</c> from a compiled plan's Union/Intersect
/// children — never constructed by a provider.
/// </summary>
public enum FusedLeafKind
{
    /// <summary>A single plain direct-relation reference. <see cref="FusedCheckLeaf.Names"/> has 1 element.</summary>
    Direct,

    /// <summary>An already-grouped sibling direct-relation set (&gt;= 2 relations, one query answers all of them). <see cref="FusedCheckLeaf.Names"/> has &gt;= 2 elements.</summary>
    MultiDirect,

    /// <summary>A single bool-attribute reference. <see cref="FusedCheckLeaf.Names"/> has 1 element.</summary>
    Attribute,

    /// <summary>An already-grouped sibling bool-attribute set. <see cref="FusedCheckLeaf.Names"/> has &gt;= 2 elements.</summary>
    MultiAttribute,

    /// <summary>
    /// A tuple-to-userset node whose plan-time analysis already proved the 2-hop-join fast path
    /// eligible: <see cref="FusedCheckLeaf.Names"/>[0] is the tupleset relation,
    /// <see cref="FusedCheckLeaf.TtuSubEntityType"/>/<see cref="FusedCheckLeaf.TtuComputedRelation"/>
    /// are the 2-hop join target.
    /// </summary>
    TupleToUserSet,
}

/// <summary>
/// One leaf of a fused boolean expression. <see cref="Names"/>' meaning depends on <see cref="Kind"/> — see
/// each <see cref="FusedLeafKind"/> member's doc. <see cref="RequireAll"/> only applies to
/// <see cref="FusedLeafKind.MultiDirect"/>/<see cref="FusedLeafKind.MultiAttribute"/> (true: every name must
/// match; false: any one match suffices) — it is this leaf's OWN combinator, independent of the fused
/// expression's outer <c>requireAll</c> (e.g. <c>org.admin and (owner or member)</c> fuses to an outer
/// AND of [TupleToUserSet, MultiDirect(RequireAll: false)]).
/// </summary>
public sealed record FusedCheckLeaf(
    FusedLeafKind Kind,
    bool Negate,
    string[] Names,
    bool RequireAll = false,
    string? TtuSubEntityType = null,
    string? TtuComputedRelation = null);
