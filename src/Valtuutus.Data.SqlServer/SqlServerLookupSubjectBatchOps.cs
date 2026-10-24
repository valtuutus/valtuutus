using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Data.Db;
using Valtuutus.Data.SqlServer.Utils;

namespace Valtuutus.Data.SqlServer;

/// <summary>
/// SqlServer commands for the LookupSubject V2 queries: the reader's own SQL text and parameter
/// writers. When the batch resolves its own token, each statement runs behind
/// <see cref="SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken"/>; both statement sets
/// are built once.
/// </summary>
internal sealed class SqlServerLookupSubjectBatchOps : ILookupSubjectBatchOps
{
    private readonly Statements _withParameter;
    private readonly Statements _batchResolved;
    // One statement per level count, index = levels; generated once, never patched per request.
    private readonly string[] _usersetLevels;
    private readonly string[] _usersetLevelsBatchResolved;

    public SqlServerLookupSubjectBatchOps(IValtuutusDbOptions dbOptions)
    {
        var q = SqlServerDataReaderProvider.GetQueries(dbOptions);
        _withParameter = new Statements(q, "");
        _batchResolved = new Statements(q, SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken);
        _usersetLevels = UsersetLevelsStatements(q.RelationsTable, dbOptions.LookupHopsPerQuery, "");
        _usersetLevelsBatchResolved = UsersetLevelsStatements(q.RelationsTable, dbOptions.LookupHopsPerQuery,
            SqlServerDataReaderProvider.DeclareBatchResolvedSnapToken);
    }

    public void AddToBatch(DbBatch batch, LookupSubjectQuery query, SnapToken? snapToken)
    {
        var statements = snapToken is null ? _batchResolved : _withParameter;
        // Batches come from SqlConnection.CreateBatch (SqlServerBatchOps), so their commands are
        // SqlBatchCommands.
        var cmd = (SqlBatchCommand)batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        switch (query.Kind)
        {
            case LookupSubjectQueryKind.Relations:
                cmd.CommandText = statements.RelationsWithEntityIds;
                SqlServerDataReaderProvider.AddRelationsWithEntityIdsParameters(parameters,
                    query.EntityType, query.Relation, query.SubjectType, query.Ids, query.SubjectRelation, snapToken);
                break;
            case LookupSubjectQueryKind.RelationsMulti:
                cmd.CommandText = statements.RelationsWithEntityIdsMultiRelation;
                SqlServerDataReaderProvider.AddRelationsWithEntityIdsMultiRelationParameters(parameters,
                    query.EntityType, query.Names, query.SubjectType, query.Ids, query.SubjectRelation, snapToken);
                break;
            case LookupSubjectQueryKind.Joined:
                cmd.CommandText = statements.RelationsJoinedByEntityIds;
                SqlServerDataReaderProvider.AddRelationsJoinedByEntityIdsParameters(parameters,
                    query.EntityType, query.Relation, query.Ids, query.SubEntityType, query.SubjectRelation, query.SubRelation,
                    query.SubjectType, snapToken);
                break;
            case LookupSubjectQueryKind.UsersetLevels:
                cmd.CommandText = (snapToken is null ? _usersetLevelsBatchResolved : _usersetLevels)[query.Levels];
                SqlServerDataReaderProvider.AddUsersetLevelsParameters(parameters, query.EntityType, query.Relation,
                    query.Ids, query.SubjectType, snapToken);
                break;
            case LookupSubjectQueryKind.Attributes:
                cmd.CommandText = SqlServerDataReaderProvider.GetAttributeInQuery(
                    statements.AttributesWithEntityIdsPrefix, query.Names.Length);
                SqlServerDataReaderProvider.AddAttributesWithEntityIdsParameters(parameters,
                    query.EntityType, query.Names, query.Ids, snapToken);
                break;
            case LookupSubjectQueryKind.Attribute:
                cmd.CommandText = statements.AttributeWithEntityIds;
                SqlServerDataReaderProvider.AddAttributeWithEntityIdsParameters(parameters,
                    query.EntityType, query.Names[0], query.Ids, snapToken);
                break;
            case LookupSubjectQueryKind.SubjectsExcluding:
                cmd.CommandText = statements.SubjectIdsExcluding;
                SqlServerDataReaderProvider.AddSubjectIdsExcludingParameters(parameters,
                    query.SubjectType, query.Ids, snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        batch.BatchCommands.Add(cmd);
    }

    // Every level reads only the tuples that name the previous level's ids, so the joins are
    // written previous-level-first and the order is forced, with nested loops: left to estimates,
    // the optimizer may drive a level from the relation's whole range instead, or run the
    // statement in parallel, whose startup each re-expanded level CTE pays again.
    private const string LevelsQueryHints = "OPTION (FORCE ORDER, LOOP JOIN, MAXDOP 1)";

    // Level 0 is the relation's tuples for the ids; level i is the tuples of the usersets level
    // i-1 lists, deduplicated so diamonds don't multiply rows. Plain subjects of the subject type
    // come from every level, usersets from the last one only.
    private static string[] UsersetLevelsStatements(string relations, int hops, string prefix)
    {
        const string snap =
            "r.created_tx_id <= @SnapToken AND (r.deleted_tx_id IS NULL OR r.deleted_tx_id > @SnapToken)";
        var statements = new string[Math.Max(hops, 2) + 1];
        for (var levels = 2; levels < statements.Length; levels++)
        {
            var sql = new StringBuilder(prefix);
            sql.Append($"""
                WITH l0 AS (
                    SELECT r.subject_type, r.subject_id, r.subject_relation FROM {IdList.Rows("@EntityIds", "ids")}
                    JOIN {relations} r ON r.entity_id = ids.id
                    WHERE {snap} AND r.entity_type = @EntityType AND r.relation = @Relation
                )
                """);
            for (var i = 1; i < levels; i++)
            {
                sql.Append($"""

                    , l{i} AS (
                        SELECT DISTINCT r.subject_type, r.subject_id, r.subject_relation FROM l{i - 1} p
                        JOIN {relations} r ON r.entity_type = p.subject_type AND r.entity_id = p.subject_id
                                          AND r.relation = p.subject_relation
                        WHERE {snap} AND p.subject_relation <> N''
                    )
                    """);
            }
            for (var i = 0; i < levels; i++)
            {
                if (i > 0) sql.Append("\nUNION ALL");
                sql.Append($"\nSELECT N'' AS entity_type, N'' AS entity_id, N'' AS relation, subject_type, subject_id, subject_relation FROM l{i}");
                sql.Append(i < levels - 1
                    ? "\nWHERE subject_type = @SubjectType AND subject_relation = N''"
                    : "\nWHERE (subject_type = @SubjectType AND subject_relation = N'') OR subject_relation <> N''");
            }
            sql.Append($"\n{LevelsQueryHints}");
            statements[levels] = sql.ToString();
        }
        return statements;
    }

    // The reader's statements behind a fixed prefix: empty for the parameter-bound set.
    private sealed class Statements(SqlServerDataReaderProvider.ReaderQueries q, string prefix)
    {
        public string RelationsWithEntityIds { get; } = prefix + q.SelectRelationsWithEntityIds;
        public string RelationsWithEntityIdsMultiRelation { get; } = prefix + q.GetRelationsWithEntityIdsMultiRelationSnap;
        public string RelationsJoinedByEntityIds { get; } = prefix + q.GetRelationsJoinedByEntityIds;
        public string AttributesWithEntityIdsPrefix { get; } = prefix + q.SelectAttributesWithEntityIdsByEntityAttributesPrefix;
        public string AttributeWithEntityIds { get; } = prefix + q.SelectAttributesWithEntityIdsByAttributeFilter;
        public string SubjectIdsExcluding { get; } = prefix + q.GetSubjectIdsExcluding;
    }
}
