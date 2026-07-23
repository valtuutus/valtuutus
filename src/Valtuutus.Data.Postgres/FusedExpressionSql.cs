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
    // so the full command text is built once per distinct fused shape instead of being
    // re-interpolated from scratch on every request. ConditionalWeakTable ties each cached entry's
    // lifetime to the leaves list's own (no manual eviction needed when a plan is GC'd).
    private static readonly ConditionalWeakTable<IReadOnlyList<FusedCheckLeaf>, string> CommandSqlCache = new();

    internal static string BuildCommandSql(IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll,
        string relationsTable, string attributesTable)
    {
        if (CommandSqlCache.TryGetValue(leaves, out var cached)) return cached;
        var expr = FusedExpressionSqlBuilder.BuildBooleanExpression(leaves, requireAll,
            (leaf, i) => LeafFragment(leaf, i, relationsTable, attributesTable));
        var sql = $"SELECT {expr}";
        CommandSqlCache.AddOrUpdate(leaves, sql);
        return sql;
    }

    internal static string LeafFragment(FusedCheckLeaf leaf, int i, string relationsTable, string attributesTable) => leaf.Kind switch
    {
        FusedLeafKind.Direct =>
            $"EXISTS(SELECT 1 FROM {relationsTable} WHERE {PostgresDataReaderProvider.SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = @relation_{i} AND subject_id = @subject_id AND subject_relation = '')",
        FusedLeafKind.MultiDirect when !leaf.RequireAll =>
            $"EXISTS(SELECT 1 FROM {relationsTable} WHERE {PostgresDataReaderProvider.SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = ANY(@relations_{i}) AND subject_id = @subject_id AND subject_relation = '')",
        FusedLeafKind.MultiDirect =>
            $"(SELECT COUNT(DISTINCT relation) FROM {relationsTable} WHERE {PostgresDataReaderProvider.SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = ANY(@relations_{i}) AND subject_id = @subject_id AND subject_relation = '') = {leaf.Names.Length}",
        FusedLeafKind.Attribute =>
            $"EXISTS(SELECT 1 FROM {attributesTable} WHERE {PostgresDataReaderProvider.SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = @attribute_{i} AND value = 'true'::jsonb)",
        FusedLeafKind.MultiAttribute when !leaf.RequireAll =>
            $"EXISTS(SELECT 1 FROM {attributesTable} WHERE {PostgresDataReaderProvider.SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = ANY(@attributes_{i}) AND value = 'true'::jsonb)",
        FusedLeafKind.MultiAttribute =>
            $"(SELECT COUNT(DISTINCT attribute) FROM {attributesTable} WHERE {PostgresDataReaderProvider.SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = ANY(@attributes_{i}) AND value = 'true'::jsonb) = {leaf.Names.Length}",
        FusedLeafKind.TupleToUserSet => $"""
            EXISTS(
                SELECT 1 FROM {relationsTable} r_main_{i}
                INNER JOIN {relationsTable} r_dep_{i}
                    ON r_dep_{i}.entity_type = r_main_{i}.subject_type AND r_dep_{i}.entity_id = r_main_{i}.subject_id
                WHERE r_main_{i}.created_tx_id <= @snap_token AND (r_main_{i}.deleted_tx_id IS NULL OR r_main_{i}.deleted_tx_id > @snap_token)
                  AND r_main_{i}.entity_type = @entity_type
                  AND r_main_{i}.entity_id = @entity_id
                  AND r_main_{i}.relation = @tuple_set_relation_{i}
                  AND r_main_{i}.subject_relation = ''
                  AND r_dep_{i}.created_tx_id <= @snap_token AND (r_dep_{i}.deleted_tx_id IS NULL OR r_dep_{i}.deleted_tx_id > @snap_token)
                  AND r_dep_{i}.relation = @computed_relation_{i}
                  AND r_dep_{i}.subject_type = @subject_type
                  AND r_dep_{i}.subject_id = @subject_id
                  AND r_dep_{i}.subject_relation = ''
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(leaf.Kind), leaf.Kind, null),
    };

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
                PostgresDataReaderProvider.AddStringParameter(parameters, $"computed_relation_{i}", leaf.TtuComputedRelation!, 64);
                break;
        }
    }
}
