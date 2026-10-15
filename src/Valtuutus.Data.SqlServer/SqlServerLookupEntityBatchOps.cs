using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Data.Db;
using Valtuutus.Data.SqlServer.Utils;

namespace Valtuutus.Data.SqlServer;

/// <summary>
/// SqlServer commands for the LookupEntity V2 queries: the reader's own SQL text and parameter
/// writers. When the batch resolves its own token, each statement runs behind
/// <see cref="SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken"/>; both statement sets
/// are built once, as are the batch-only <see cref="LookupEntityQueryKind.RelationsLevels"/> statements.
/// </summary>
internal sealed class SqlServerLookupEntityBatchOps : ILookupEntityBatchOps
{
    private readonly SqlServerDataReaderProvider.ReaderQueries _withParameter;
    private readonly SqlServerDataReaderProvider.ReaderQueries _batchResolved;
    // One statement per level count, index = levels, built with the catalog.
    private readonly string[] _relationsLevels;
    private readonly string[] _relationsLevelsBatchResolved;

    public SqlServerLookupEntityBatchOps(IValtuutusDbOptions dbOptions)
    {
        _withParameter = SqlServerDataReaderProvider.GetQueries(dbOptions);
        _batchResolved = WithPrefix(_withParameter, SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken);
        _relationsLevels = RelationsLevelsStatements(_withParameter.RelationsTable, dbOptions.LookupHopsPerQuery, "");
        _relationsLevelsBatchResolved = RelationsLevelsStatements(_withParameter.RelationsTable,
            dbOptions.LookupHopsPerQuery, SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken);
    }

    public void AddToBatch(DbBatch batch, LookupEntityQuery query, SnapToken? snapToken)
    {
        var q = snapToken is null ? _batchResolved : _withParameter;
        // Batches come from SqlConnection.CreateBatch (SqlServerBatchOps), so their commands are
        // SqlBatchCommands.
        var cmd = (SqlBatchCommand)batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        var scoped = query.Scope is not null;
        switch (query.Kind)
        {
            case LookupEntityQueryKind.Relations:
            {
                var ids = AsArray(query.Ids);
                cmd.CommandText = SqlServerDataReaderProvider.RelationsWithSubjectIdsStatement(q, scoped, ids.Length == 1);
                SqlServerDataReaderProvider.AddRelationsWithSubjectIdsParameters(parameters, query.EntityType,
                    query.Relation, query.SubjectType, query.SubjectRelation, ids, query.Scope, snapToken);
                break;
            }
            case LookupEntityQueryKind.RelationsMulti:
            {
                var ids = AsArray(query.Ids);
                cmd.CommandText = SqlServerDataReaderProvider.RelationsWithSubjectIdsMultiRelationStatement(q, scoped, ids.Length == 1);
                SqlServerDataReaderProvider.AddRelationsWithSubjectIdsMultiRelationParameters(parameters,
                    query.EntityType, query.Names, query.SubjectType, query.SubjectRelation, ids, query.Scope, snapToken);
                break;
            }
            case LookupEntityQueryKind.Joined:
                cmd.CommandText = SqlServerDataReaderProvider.RelationsJoinedStatement(q, scoped);
                SqlServerDataReaderProvider.AddRelationsJoinedParameters(parameters, query.EntityType, query.Relation,
                    query.SubEntityType, query.SubjectRelation, query.SubRelation, query.SubjectType,
                    query.Ids.First(), query.Scope, snapToken);
                break;
            case LookupEntityQueryKind.Attributes:
                cmd.CommandText = SqlServerDataReaderProvider.GetAttributeInQuery(
                    SqlServerDataReaderProvider.AttributesStatementPrefix(q, scoped, hasEntityId: false),
                    query.Names.Length);
                SqlServerDataReaderProvider.AddAttributesParameters(parameters, query.EntityType, query.Names,
                    entityId: null, query.Scope, snapToken);
                break;
            case LookupEntityQueryKind.AttributesWithIds:
                cmd.CommandText = SqlServerDataReaderProvider.GetAttributeInQuery(
                    q.SelectAttributesWithEntityIdsByEntityAttributesPrefix, query.Names.Length);
                SqlServerDataReaderProvider.AddAttributesWithEntityIdsParameters(parameters, query.EntityType,
                    query.Names, query.Ids, snapToken);
                break;
            case LookupEntityQueryKind.Attribute:
                cmd.CommandText = SqlServerDataReaderProvider.AttributeStatement(q, hasEntityId: false);
                SqlServerDataReaderProvider.AddAttributeParameters(parameters, query.EntityType, query.Names[0],
                    entityId: null, snapToken);
                break;
            case LookupEntityQueryKind.EntitiesExcluding:
                cmd.CommandText = q.GetEntityIdsExcluding;
                SqlServerDataReaderProvider.AddEntityIdsExcludingParameters(parameters, query.EntityType, query.Ids,
                    snapToken);
                break;
            case LookupEntityQueryKind.RelationsLevels:
                cmd.CommandText = (snapToken is null ? _relationsLevelsBatchResolved : _relationsLevels)[query.Levels];
                SqlServerDataReaderProvider.AddRelationsLevelsParameters(parameters, query.EntityType, query.Relation,
                    query.Ids, query.LevelOneIds, snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        batch.BatchCommands.Add(cmd);
    }

    private static string[] AsArray(IReadOnlyCollection<string> ids) => ids as string[] ?? ids.ToArray();

    // Level 1 is the relation's entities whose tuples name an id through the relation, plus the
    // level-one ids; level i follows level i-1 the same way, deduplicated so diamonds don't
    // multiply rows. Each entity is reported once, at the first level reaching it, carrying the
    // relation when that level is the last.
    private static string[] RelationsLevelsStatements(string relations, int hops, string prefix)
    {
        const string snap = SqlServerDataReaderProvider.SnapPredicate;
        // Qualified: the levels after the first join relation_tuples to the previous level's CTE.
        const string joinedSnap =
            "r.created_tx_id <= @SnapToken AND (r.deleted_tx_id IS NULL OR r.deleted_tx_id > @SnapToken)";
        var statements = new string[Math.Max(hops, 2) + 1];
        for (var levels = 2; levels < statements.Length; levels++)
        {
            var sql = new StringBuilder(prefix);
            sql.Append($"""
                WITH l1 AS (
                    SELECT entity_id FROM {relations}
                    WHERE {snap}
                      AND entity_type = @EntityType AND relation = @Relation AND subject_type = @EntityType
                      AND subject_id IN ({IdList.From("@Ids")}) AND subject_relation = @Relation
                    UNION
                    {IdList.From("@LevelOneIds")}
                )
                """);
            for (var i = 2; i <= levels; i++)
            {
                sql.Append($"""

                    , l{i} AS (
                        SELECT DISTINCT r.entity_id FROM {relations} r
                        JOIN l{i - 1} p ON r.subject_id = p.entity_id
                        WHERE {joinedSnap}
                          AND r.entity_type = @EntityType AND r.relation = @Relation
                          AND r.subject_type = @EntityType AND r.subject_relation = @Relation
                    )
                    """);
            }
            sql.Append($"""

                SELECT @EntityType AS entity_type, entity_id,
                       CASE WHEN MIN(lvl) = {levels} THEN @Relation ELSE N'' END AS relation,
                       N'' AS subject_type, N'' AS subject_id, N'' AS subject_relation
                FROM (
                """);
            for (var i = 1; i <= levels; i++)
            {
                if (i > 1) sql.Append("\n    UNION ALL");
                sql.Append($"\n    SELECT entity_id, {i} AS lvl FROM l{i}");
            }
            sql.Append("\n) x\nGROUP BY entity_id");
            statements[levels] = sql.ToString();
        }
        return statements;
    }

    // Every statement the LookupEntity choosers can return, behind a fixed prefix.
    private static SqlServerDataReaderProvider.ReaderQueries WithPrefix(
        SqlServerDataReaderProvider.ReaderQueries q, string prefix) => q with
    {
        GetRelationsWithSingleSubjectSnap = prefix + q.GetRelationsWithSingleSubjectSnap,
        GetRelationsWithMultiSubjectSnap = prefix + q.GetRelationsWithMultiSubjectSnap,
        GetRelationsWithSingleSubjectScoped = prefix + q.GetRelationsWithSingleSubjectScoped,
        GetRelationsWithMultiSubjectScoped = prefix + q.GetRelationsWithMultiSubjectScoped,
        GetRelationsWithSingleSubjectMultiRelationSnap = prefix + q.GetRelationsWithSingleSubjectMultiRelationSnap,
        GetRelationsWithMultiSubjectMultiRelationSnap = prefix + q.GetRelationsWithMultiSubjectMultiRelationSnap,
        GetRelationsWithSingleSubjectMultiRelationScoped = prefix + q.GetRelationsWithSingleSubjectMultiRelationScoped,
        GetRelationsWithMultiSubjectMultiRelationScoped = prefix + q.GetRelationsWithMultiSubjectMultiRelationScoped,
        GetRelationsJoined = prefix + q.GetRelationsJoined,
        GetRelationsJoinedScoped = prefix + q.GetRelationsJoinedScoped,
        GetAttributesDictScopedPrefix = prefix + q.GetAttributesDictScopedPrefix,
        SelectAttributesByEntityAttributesPrefix = prefix + q.SelectAttributesByEntityAttributesPrefix,
        SelectAttributesWithEntityIdsByEntityAttributesPrefix = prefix + q.SelectAttributesWithEntityIdsByEntityAttributesPrefix,
        SelectAttributesByEntityAttributeFilter = prefix + q.SelectAttributesByEntityAttributeFilter,
        SelectAttributesByEntityAttributeFilterWithEntityId = prefix + q.SelectAttributesByEntityAttributeFilterWithEntityId,
        SelectAttributesByEntityAttributesWithEntityIdPrefix = prefix + q.SelectAttributesByEntityAttributesWithEntityIdPrefix,
        GetEntityIdsExcluding = prefix + q.GetEntityIdsExcluding,
    };
}
