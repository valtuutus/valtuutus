namespace Valtuutus.Data.Db;

/// <summary>
/// Joins per-leaf SQL fragments into one boolean expression — the provider-agnostic half of
/// building a fused expression's SQL text. A provider supplies <c>fragment</c>, returning an
/// EXISTS(...)-or-count-comparison fragment for leaf <c>i</c> already using index-suffixed
/// parameter names (<c>@relation_{i}</c>, ...) so leaves of the same kind never collide. This
/// class only decides join order/combinator/negation wrapping — it never touches parameters.
/// </summary>
public static class FusedExpressionSqlBuilder
{
    public static string BuildBooleanExpression(IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll,
        Func<FusedCheckLeaf, int, string> fragment)
    {
        var combinator = requireAll ? " AND " : " OR ";
        var parts = new string[leaves.Count];
        for (var i = 0; i < leaves.Count; i++)
        {
            var frag = fragment(leaves[i], i);
            parts[i] = leaves[i].Negate ? $"NOT {frag}" : frag;
        }
        return $"({string.Join(combinator, parts)})";
    }
}
