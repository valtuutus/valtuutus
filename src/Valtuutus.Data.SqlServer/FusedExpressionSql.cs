using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Valtuutus.Data.Db;
using Valtuutus.Data.SqlServer.Utils;

namespace Valtuutus.Data.SqlServer;

// Shared by SqlServerDataReaderProvider.HasFusedExpression and
// SqlServerBatchOps.AddHasFusedExpressionToBatch: both paths build the per-leaf fragment and
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
        var sql = $"SELECT CASE WHEN {expr} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        CommandSqlCache.AddOrUpdate(leaves, sql);
        return sql;
    }

    internal static string LeafFragment(FusedCheckLeaf leaf, int i, string relationsTable, string attributesTable) => leaf.Kind switch
    {
        FusedLeafKind.Direct =>
            $"EXISTS(SELECT 1 FROM {relationsTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND relation = @Relation_{i} AND subject_id = @SubjectId AND subject_relation = '')",
        FusedLeafKind.MultiDirect when !leaf.RequireAll =>
            $"EXISTS(SELECT 1 FROM {relationsTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND relation IN (SELECT id FROM @Relations_{i}) AND subject_id = @SubjectId AND subject_relation = '')",
        FusedLeafKind.MultiDirect =>
            $"(SELECT COUNT(DISTINCT relation) FROM {relationsTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND relation IN (SELECT id FROM @Relations_{i}) AND subject_id = @SubjectId AND subject_relation = '') = {leaf.Names.Length}",
        FusedLeafKind.Attribute =>
            $"EXISTS(SELECT 1 FROM {attributesTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND attribute = @Attribute_{i} AND value = 'true')",
        FusedLeafKind.MultiAttribute when !leaf.RequireAll =>
            $"EXISTS(SELECT 1 FROM {attributesTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND attribute IN (SELECT id FROM @Attributes_{i}) AND value = 'true')",
        FusedLeafKind.MultiAttribute =>
            $"(SELECT COUNT(DISTINCT attribute) FROM {attributesTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND attribute IN (SELECT id FROM @Attributes_{i}) AND value = 'true') = {leaf.Names.Length}",
        FusedLeafKind.TupleToUserSet => $"""
            EXISTS(
                SELECT 1 FROM {relationsTable} r_main_{i}
                INNER JOIN {relationsTable} r_dep_{i}
                    ON r_dep_{i}.entity_type = r_main_{i}.subject_type AND r_dep_{i}.entity_id = r_main_{i}.subject_id
                WHERE r_main_{i}.created_tx_id <= @SnapToken AND (r_main_{i}.deleted_tx_id IS NULL OR r_main_{i}.deleted_tx_id > @SnapToken)
                  AND r_main_{i}.entity_type = @EntityType
                  AND r_main_{i}.entity_id = @EntityId
                  AND r_main_{i}.relation = @TupleSetRelation_{i}
                  AND r_main_{i}.subject_relation = ''
                  AND r_dep_{i}.created_tx_id <= @SnapToken AND (r_dep_{i}.deleted_tx_id IS NULL OR r_dep_{i}.deleted_tx_id > @SnapToken)
                  AND r_dep_{i}.relation = @ComputedRelation_{i}
                  AND r_dep_{i}.subject_type = @SubjectType
                  AND r_dep_{i}.subject_id = @SubjectId
                  AND r_dep_{i}.subject_relation = ''
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(leaf.Kind), leaf.Kind, null),
    };

    internal static void WriteLeafParameters(SqlParameterCollection parameters, FusedCheckLeaf leaf, int i, string tvpListIdsTypeName)
    {
        switch (leaf.Kind)
        {
            case FusedLeafKind.Direct:
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@Relation_{i}", leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiDirect:
                TvpHelper.AsTvpParameter(leaf.Names, tvpListIdsTypeName).AddParameter(parameters, $"@Relations_{i}");
                break;
            case FusedLeafKind.Attribute:
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@Attribute_{i}", leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiAttribute:
                TvpHelper.AsTvpParameter(leaf.Names, tvpListIdsTypeName).AddParameter(parameters, $"@Attributes_{i}");
                break;
            case FusedLeafKind.TupleToUserSet:
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@TupleSetRelation_{i}", leaf.Names[0], 64);
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@ComputedRelation_{i}", leaf.TtuComputedRelation!, 64);
                break;
        }
    }
}
