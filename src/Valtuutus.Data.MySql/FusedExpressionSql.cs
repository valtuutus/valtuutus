using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

// Builds and compiles the fused-expression statement and writes its per-leaf parameters, so every
// caller of a fused shape sends the same text with the same parameters.
internal static class FusedExpressionSql
{
    // Keyed by the leaves list's reference identity, which is stable for a compiled plan's whole
    // lifetime, so each fused shape is built and compiled once per catalog. The ConditionalWeakTable
    // ties each entry's lifetime to the leaves list. Catalogs are cached singletons, compared by
    // reference, so the bound-token and batch-resolved-token catalogs each get their own text for a
    // shape.
    private static readonly ConditionalWeakTable<IReadOnlyList<FusedCheckLeaf>, ConcurrentDictionary<MySqlQueries, MySqlStatement>> StatementCache = new();

    internal static MySqlStatement BuildStatement(IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll, MySqlQueries q)
    {
        var perCatalog = StatementCache.GetValue(leaves,
            static _ => new ConcurrentDictionary<MySqlQueries, MySqlStatement>(ReferenceEqualityComparer.Instance));
        if (perCatalog.TryGetValue(q, out var cached)) return cached;
        var expr = FusedExpressionSqlBuilder.BuildBooleanExpression(leaves, requireAll, (leaf, i) => LeafFragment(leaf, i, q));
        return perCatalog.GetOrAdd(q, MySqlQueries.Statement($"SELECT {expr}"));
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
                $"EXISTS(SELECT {hr}1 FROM {rt} {r} WHERE {relationFilter} AND {r}.relation = {Relation.Sql(i)})",
            FusedLeafKind.MultiDirect when !leaf.RequireAll =>
                $"EXISTS(SELECT 1 FROM {JsonNames(Relations.Sql(i), n)} JOIN {rt} {r} ON {r}.relation = {n}.name WHERE {relationFilter})",
            FusedLeafKind.MultiDirect =>
                $"(SELECT COUNT(DISTINCT {r}.relation) FROM {JsonNames(Relations.Sql(i), n)} JOIN {rt} {r} ON {r}.relation = {n}.name WHERE {relationFilter}) = {DistinctCount(leaf.Names)}",
            FusedLeafKind.Attribute =>
                $"EXISTS(SELECT {ha}1 FROM {at} {a} WHERE {attributeFilter} AND {a}.attribute = {Attribute.Sql(i)})",
            FusedLeafKind.MultiAttribute when !leaf.RequireAll =>
                $"EXISTS(SELECT 1 FROM {JsonNames(Attributes.Sql(i), n)} JOIN {at} {a} ON {a}.attribute = {n}.name WHERE {attributeFilter})",
            FusedLeafKind.MultiAttribute =>
                $"(SELECT COUNT(DISTINCT {a}.attribute) FROM {JsonNames(Attributes.Sql(i), n)} JOIN {at} {a} ON {a}.attribute = {n}.name WHERE {attributeFilter}) = {DistinctCount(leaf.Names)}",
            FusedLeafKind.TupleToUserSet => MySqlQueries.TwoHop(rt, q.Snap,
                TupleSetRelation.Sql(i), SubEntityType.Sql(i), "''", ComputedRelation.Sql(i), $"{i}"),
            _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf.Kind, null),
        };
    }

    // Leaf parameter names carry the leaf index. The statement and the binder take them from these
    // sets, so they always agree, and binding builds no name strings for the first leaves.
    private static readonly LeafNames Relation = new("relation_");
    private static readonly LeafNames Relations = new("relations_");
    private static readonly LeafNames Attribute = new("attribute_");
    private static readonly LeafNames Attributes = new("attributes_");
    private static readonly LeafNames TupleSetRelation = new("tuple_set_relation_");
    private static readonly LeafNames SubEntityType = new("sub_entity_type_");
    private static readonly LeafNames ComputedRelation = new("computed_relation_");

    private sealed class LeafNames(string prefix)
    {
        private const int Cached = 16;
        private readonly string[] _bound = Enumerable.Range(0, Cached).Select(i => Build(prefix, i)).ToArray();

        // The name the statement text reads.
        public string Sql(int leafIndex) => "@" + Bound(leafIndex);

        // The name the parameter is bound under.
        public string Bound(int leafIndex) => leafIndex < Cached ? _bound[leafIndex] : Build(prefix, leafIndex);

        private static string Build(string prefix, int leafIndex) =>
            prefix + leafIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // The JSON array holds each name once, so an all-of leaf compares against the distinct count.
    private static int DistinctCount(string[] names) => names.Distinct(StringComparer.Ordinal).Count();

    internal static void WriteLeafParameters(DbParameterCollection parameters, FusedCheckLeaf leaf, int i)
    {
        switch (leaf.Kind)
        {
            case FusedLeafKind.Direct:
                AddString(parameters, Relation.Bound(i), leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiDirect:
                AddJsonArray(parameters, Relations.Bound(i), leaf.Names);
                break;
            case FusedLeafKind.Attribute:
                AddString(parameters, Attribute.Bound(i), leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiAttribute:
                AddJsonArray(parameters, Attributes.Bound(i), leaf.Names);
                break;
            case FusedLeafKind.TupleToUserSet:
                AddString(parameters, TupleSetRelation.Bound(i), leaf.Names[0], 64);
                AddString(parameters, SubEntityType.Bound(i), leaf.TtuSubEntityType!, 256);
                AddString(parameters, ComputedRelation.Bound(i), leaf.TtuComputedRelation!, 64);
                break;
        }
    }
}
