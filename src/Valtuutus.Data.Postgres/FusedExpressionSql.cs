using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

// Shared by PostgresDataReaderProvider.HasFusedExpression and
// PostgresBatchOps.AddHasFusedExpressionToBatch: both paths build the per-leaf fragment and
// parameters from here, so they can never drift apart.
internal static class FusedExpressionSql
{
    // Keyed by the leaves list's own reference identity — stable for a FusedExpressionOp's whole
    // lifetime (constructed once per compiled plan, reused across every Check against that plan) —
    // so the full statement is built and compiled once per distinct fused shape instead of being
    // re-interpolated from scratch on every request. ConditionalWeakTable ties each cached entry's
    // lifetime to the leaves list's own (no manual eviction needed when a plan is GC'd).
    // One cache per snap-token flavor: the batch-resolved-token catalog's text differs only in what the
    // snap-token predicates compare against.
    private static readonly ConditionalWeakTable<IReadOnlyList<FusedCheckLeaf>, PgStatement> StatementCache = new();
    private static readonly ConditionalWeakTable<IReadOnlyList<FusedCheckLeaf>, PgStatement> BatchResolvedSnapTokenStatementCache = new();

    internal static PgStatement BuildStatement(IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll,
        string relationsTable, string attributesTable, int twoHopEntityScanLimit, bool batchResolvedSnapToken)
    {
        var cache = batchResolvedSnapToken ? BatchResolvedSnapTokenStatementCache : StatementCache;
        if (cache.TryGetValue(leaves, out var cached)) return cached;
        var snap = batchResolvedSnapToken ? PostgresDataReaderProvider.BatchResolvedSnapTokenExpression : "@snap_token";
        var expr = FusedExpressionSqlBuilder.BuildBooleanExpression(leaves, requireAll,
            (leaf, i) => LeafFragment(leaf, i, relationsTable, attributesTable, twoHopEntityScanLimit, snap));
        var statement = PgStatement.Compile($"SELECT {expr}");
        cache.AddOrUpdate(leaves, statement);
        return statement;
    }

    internal static string LeafFragment(FusedCheckLeaf leaf, int i, string relationsTable, string attributesTable,
        int twoHopEntityScanLimit, string snap)
    {
        var snapPredicate = PostgresDataReaderProvider.SnapTokenPredicateFor(snap);
        return leaf.Kind switch
        {
        FusedLeafKind.Direct =>
            $"EXISTS(SELECT 1 FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = @relation_{i} AND subject_id = @subject_id AND subject_relation = '')",
        FusedLeafKind.MultiDirect when !leaf.RequireAll =>
            $"EXISTS(SELECT 1 FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = ANY(@relations_{i}) AND subject_id = @subject_id AND subject_relation = '')",
        FusedLeafKind.MultiDirect =>
            $"(SELECT COUNT(DISTINCT relation) FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = ANY(@relations_{i}) AND subject_id = @subject_id AND subject_relation = '') = {leaf.Names.Length}",
        FusedLeafKind.Attribute =>
            $"EXISTS(SELECT 1 FROM {attributesTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = @attribute_{i} AND value = 'true'::jsonb)",
        FusedLeafKind.MultiAttribute when !leaf.RequireAll =>
            $"EXISTS(SELECT 1 FROM {attributesTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = ANY(@attributes_{i}) AND value = 'true'::jsonb)",
        FusedLeafKind.MultiAttribute =>
            $"(SELECT COUNT(DISTINCT attribute) FROM {attributesTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = ANY(@attributes_{i}) AND value = 'true'::jsonb) = {leaf.Names.Length}",
        FusedLeafKind.TupleToUserSet => PostgresDataReaderProvider.TwoHopExists(relationsTable, snap, twoHopEntityScanLimit,
            $"@tuple_set_relation_{i}", $"@sub_entity_type_{i}", "''", $"@computed_relation_{i}"),
        _ => throw new ArgumentOutOfRangeException(nameof(leaf.Kind), leaf.Kind, null),
        };
    }

    internal static void WriteLeafParameters(NpgsqlParameterCollection parameters, FusedCheckLeaf leaf, int i)
    {
        switch (leaf.Kind)
        {
            case FusedLeafKind.Direct:
                PostgresDataReaderProvider.AddStringParameter(parameters, $"relation_{i}", leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiDirect:
                parameters.Add(new NpgsqlParameter<string[]>($"relations_{i}", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = leaf.Names });
                break;
            case FusedLeafKind.Attribute:
                PostgresDataReaderProvider.AddStringParameter(parameters, $"attribute_{i}", leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiAttribute:
                parameters.Add(new NpgsqlParameter<string[]>($"attributes_{i}", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = leaf.Names });
                break;
            case FusedLeafKind.TupleToUserSet:
                PostgresDataReaderProvider.AddStringParameter(parameters, $"tuple_set_relation_{i}", leaf.Names[0], 64);
                PostgresDataReaderProvider.AddStringParameter(parameters, $"sub_entity_type_{i}", leaf.TtuSubEntityType!, 256);
                PostgresDataReaderProvider.AddStringParameter(parameters, $"computed_relation_{i}", leaf.TtuComputedRelation!, 64);
                break;
        }
    }
}
