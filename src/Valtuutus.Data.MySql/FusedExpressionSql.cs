using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

// Builds the fused-expression statement and binds its per-leaf parameters, so every caller of a
// fused shape sends the same text with the same parameters.
internal static class FusedExpressionSql
{
    // Keyed by the leaves list's reference identity, which is stable for a compiled plan's whole
    // lifetime, so each fused shape is built once per catalog. The ConditionalWeakTable ties each
    // entry's lifetime to the leaves list. Catalogs are cached singletons, compared by reference.
    private static readonly ConditionalWeakTable<IReadOnlyList<FusedCheckLeaf>, ConcurrentDictionary<MySqlQueries, string>> StatementCache = new();

    internal static string BuildStatement(IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll, MySqlQueries q)
    {
        var perCatalog = StatementCache.GetValue(leaves,
            static _ => new ConcurrentDictionary<MySqlQueries, string>(ReferenceEqualityComparer.Instance));
        if (perCatalog.TryGetValue(q, out var cached)) return cached;
        var expr = FusedExpressionSqlBuilder.BuildBooleanExpression(leaves, requireAll, (leaf, i) => LeafFragment(leaf, i, q));
        return perCatalog.GetOrAdd(q, Compact($"SELECT {expr}"));
    }

    // Every alias carries the leaf index, so the subqueries of different leaves never share one.
    internal static string LeafFragment(FusedCheckLeaf leaf, int i, MySqlQueries q)
    {
        var rt = q.RelationsTable;
        var at = q.AttributesTable;
        var r = $"r{i}";
        var a = $"a{i}";
        var n = $"n{i}";
        var hr = PointHint(q.ServerType, r);
        var ha = PointHint(q.ServerType, a);
        var relationFilter = $"{Snap(r, q.Snap)} AND {r}.entity_type = @entity_type AND {r}.entity_id = @entity_id AND {r}.subject_id = @subject_id AND {r}.subject_relation = ''";
        var attributeFilter = $"{Snap(a, q.Snap)} AND {a}.entity_type = @entity_type AND {a}.entity_id = @entity_id AND {a}.value = 'true'";
        return leaf.Kind switch
        {
            FusedLeafKind.Direct =>
                $"EXISTS(SELECT {hr}1 FROM {rt} {r} WHERE {relationFilter} AND {r}.relation = @relation_{i})",
            FusedLeafKind.MultiDirect when !leaf.RequireAll =>
                $"EXISTS(SELECT 1 FROM {JsonNames($"@relations_{i}", n)} JOIN {rt} {r} ON {r}.relation = {n}.name WHERE {relationFilter})",
            FusedLeafKind.MultiDirect =>
                $"(SELECT COUNT(DISTINCT {r}.relation) FROM {JsonNames($"@relations_{i}", n)} JOIN {rt} {r} ON {r}.relation = {n}.name WHERE {relationFilter}) = {DistinctCount(leaf.Names)}",
            FusedLeafKind.Attribute =>
                $"EXISTS(SELECT {ha}1 FROM {at} {a} WHERE {attributeFilter} AND {a}.attribute = @attribute_{i})",
            FusedLeafKind.MultiAttribute when !leaf.RequireAll =>
                $"EXISTS(SELECT 1 FROM {JsonNames($"@attributes_{i}", n)} JOIN {at} {a} ON {a}.attribute = {n}.name WHERE {attributeFilter})",
            FusedLeafKind.MultiAttribute =>
                $"(SELECT COUNT(DISTINCT {a}.attribute) FROM {JsonNames($"@attributes_{i}", n)} JOIN {at} {a} ON {a}.attribute = {n}.name WHERE {attributeFilter}) = {DistinctCount(leaf.Names)}",
            FusedLeafKind.TupleToUserSet => MySqlQueries.TwoHop(rt, q.Snap,
                $"@tuple_set_relation_{i}", $"@sub_entity_type_{i}", "''", $"@computed_relation_{i}", $"{i}"),
            _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf.Kind, null),
        };
    }

    // The JSON array holds each name once, so an all-of leaf compares against the distinct count.
    private static int DistinctCount(string[] names) => names.Distinct(StringComparer.Ordinal).Count();

    internal static void WriteLeafParameters(DbParameterCollection parameters, FusedCheckLeaf leaf, int i)
    {
        switch (leaf.Kind)
        {
            case FusedLeafKind.Direct:
                AddString(parameters, $"@relation_{i}", leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiDirect:
                AddJsonArray(parameters, $"@relations_{i}", leaf.Names);
                break;
            case FusedLeafKind.Attribute:
                AddString(parameters, $"@attribute_{i}", leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiAttribute:
                AddJsonArray(parameters, $"@attributes_{i}", leaf.Names);
                break;
            case FusedLeafKind.TupleToUserSet:
                AddString(parameters, $"@tuple_set_relation_{i}", leaf.Names[0], 64);
                AddString(parameters, $"@sub_entity_type_{i}", leaf.TtuSubEntityType!, 256);
                AddString(parameters, $"@computed_relation_{i}", leaf.TtuComputedRelation!, 64);
                break;
        }
    }
}
