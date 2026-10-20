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
    // The batch-resolved-token form is the same text behind SqlServerDataReaderProvider's
    // DeclareBatchResolvedSnapToken prefix, cached separately.
    private static readonly ConditionalWeakTable<IReadOnlyList<FusedCheckLeaf>, string> CommandSqlCache = new();
    private static readonly ConditionalWeakTable<IReadOnlyList<FusedCheckLeaf>, string> BatchResolvedSnapTokenCommandSqlCache = new();

    internal static string BuildCommandSql(IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll,
        string relationsTable, string attributesTable, int twoHopEntityScanLimit, bool batchResolvedSnapToken)
    {
        if (batchResolvedSnapToken)
        {
            if (BatchResolvedSnapTokenCommandSqlCache.TryGetValue(leaves, out var cachedBatchResolved))
                return cachedBatchResolved;
            var batchResolved = SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken
                + BuildCommandSql(leaves, requireAll, relationsTable, attributesTable, twoHopEntityScanLimit,
                    batchResolvedSnapToken: false);
            BatchResolvedSnapTokenCommandSqlCache.AddOrUpdate(leaves, batchResolved);
            return batchResolved;
        }

        if (CommandSqlCache.TryGetValue(leaves, out var cached)) return cached;
        // A tuple-to-userset leaf is answered by statements ahead of the fused SELECT (see
        // SqlServerDataReaderProvider.TwoHopExistsInto); the SELECT reads its variable.
        var twoHops = new System.Text.StringBuilder();
        for (var i = 0; i < leaves.Count; i++)
        {
            if (leaves[i].Kind != FusedLeafKind.TupleToUserSet) continue;
            twoHops.Append($"DECLARE {TwoHopHitVariable(i)} int;\n");
            twoHops.Append(SqlServerDataReaderProvider.TwoHopExistsInto(TwoHopHitVariable(i), relationsTable,
                twoHopEntityScanLimit, $"@TupleSetRelation_{i}", $"@SubEntityType_{i}", "''", $"@ComputedRelation_{i}"));
        }
        var expr = FusedExpressionSqlBuilder.BuildBooleanExpression(leaves, requireAll,
            (leaf, i) => LeafFragment(leaf, i, relationsTable, attributesTable));
        var sql = $"{twoHops}SELECT CASE WHEN {expr} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        CommandSqlCache.AddOrUpdate(leaves, sql);
        return sql;
    }

    internal static string LeafFragment(FusedCheckLeaf leaf, int i, string relationsTable, string attributesTable) => leaf.Kind switch
    {
        FusedLeafKind.Direct =>
            $"EXISTS(SELECT 1 FROM {relationsTable} {SqlServerDataReaderProvider.DirectTupleIndexHint} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND relation = @Relation_{i} AND subject_id = @SubjectId AND subject_relation = '')",
        FusedLeafKind.MultiDirect when !leaf.RequireAll =>
            $"EXISTS(SELECT 1 FROM {relationsTable} {SqlServerDataReaderProvider.DirectTupleIndexHint} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND relation IN ({IdList.From($"@Relations_{i}")}) AND subject_id = @SubjectId AND subject_relation = '')",
        FusedLeafKind.MultiDirect =>
            $"(SELECT COUNT(DISTINCT relation) FROM {relationsTable} {SqlServerDataReaderProvider.DirectTupleIndexHint} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND relation IN ({IdList.From($"@Relations_{i}")}) AND subject_id = @SubjectId AND subject_relation = '') = {leaf.Names.Length}",
        FusedLeafKind.Attribute =>
            $"EXISTS(SELECT 1 FROM {attributesTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND attribute = @Attribute_{i} AND value = 'true')",
        FusedLeafKind.MultiAttribute when !leaf.RequireAll =>
            $"EXISTS(SELECT 1 FROM {attributesTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND attribute IN ({IdList.From($"@Attributes_{i}")}) AND value = 'true')",
        FusedLeafKind.MultiAttribute =>
            $"(SELECT COUNT(DISTINCT attribute) FROM {attributesTable} WHERE {SqlServerDataReaderProvider.SnapPredicate} AND entity_type = @EntityType AND entity_id = @EntityId AND attribute IN ({IdList.From($"@Attributes_{i}")}) AND value = 'true') = {leaf.Names.Length}",
        FusedLeafKind.TupleToUserSet => $"{TwoHopHitVariable(i)} = 1",
        _ => throw new ArgumentOutOfRangeException(nameof(leaf.Kind), leaf.Kind, null),
    };

    private static string TwoHopHitVariable(int i) => $"@TwoHopHit_{i}";

    internal static void WriteLeafParameters(SqlParameterCollection parameters, FusedCheckLeaf leaf, int i)
    {
        switch (leaf.Kind)
        {
            case FusedLeafKind.Direct:
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@Relation_{i}", leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiDirect:
                IdList.AddParameter(parameters, $"@Relations_{i}", leaf.Names);
                break;
            case FusedLeafKind.Attribute:
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@Attribute_{i}", leaf.Names[0], 64);
                break;
            case FusedLeafKind.MultiAttribute:
                IdList.AddParameter(parameters, $"@Attributes_{i}", leaf.Names);
                break;
            case FusedLeafKind.TupleToUserSet:
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@TupleSetRelation_{i}", leaf.Names[0], 64);
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@SubEntityType_{i}", leaf.TtuSubEntityType!, 256);
                SqlServerDataReaderProvider.AddStringParameter(parameters, $"@ComputedRelation_{i}", leaf.TtuComputedRelation!, 64);
                break;
        }
    }
}
