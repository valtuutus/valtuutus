using System.Data.Common;
using System.Text;
using Npgsql;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

/// <summary>
/// Postgres commands for the LookupSubject V2 queries: the reader's own catalog statements and
/// parameter writers, from the batch-resolved-token catalog when the batch resolves its own token.
/// </summary>
internal sealed class PostgresLookupSubjectBatchOps(ValtuutusPostgresOptions dbOptions) : ILookupSubjectBatchOps
{
    private readonly PostgresDataReaderProvider.ReaderQueries _withParameter =
        PostgresDataReaderProvider.GetQueries(dbOptions);
    private readonly PostgresDataReaderProvider.ReaderQueries _batchResolved =
        PostgresDataReaderProvider.GetBatchResolvedSnapTokenQueries(dbOptions);
    // One statement per level count, index = levels; generated once, never patched per request.
    private readonly PgStatement[] _usersetLevels = UsersetLevelsStatements(dbOptions, "@snap_token");
    private readonly PgStatement[] _usersetLevelsBatchResolved =
        UsersetLevelsStatements(dbOptions, PostgresDataReaderProvider.BatchResolvedSnapTokenExpression);

    public void AddToBatch(DbBatch batch, LookupSubjectQuery query, SnapToken? snapToken)
    {
        var q = snapToken is null ? _batchResolved : _withParameter;
        // Batches come from NpgsqlDataSource.CreateBatch (PostgresBatchOps), so their commands are
        // NpgsqlBatchCommands.
        var cmd = (NpgsqlBatchCommand)batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        PgStatement statement;
        switch (query.Kind)
        {
            case LookupSubjectQueryKind.Relations:
                statement = q.SelectRelationsWithEntityIds;
                PostgresDataReaderProvider.AddRelationsWithEntityIdsParameters(parameters, query.EntityType,
                    query.Relation, query.SubjectType, query.Ids, query.SubjectRelation, snapToken);
                break;
            case LookupSubjectQueryKind.RelationsMulti:
                statement = q.GetRelationsWithEntityIdsMultiRelationSnap;
                PostgresDataReaderProvider.AddRelationsWithEntityIdsMultiRelationParameters(parameters,
                    query.EntityType, query.Names, query.SubjectType, query.Ids, query.SubjectRelation, snapToken);
                break;
            case LookupSubjectQueryKind.Joined:
                statement = q.GetRelationsJoinedByEntityIds;
                PostgresDataReaderProvider.AddRelationsJoinedByEntityIdsParameters(parameters, query.EntityType,
                    query.Relation, query.Ids, query.SubEntityType, query.SubjectRelation, query.SubRelation, query.SubjectType,
                    snapToken);
                break;
            case LookupSubjectQueryKind.UsersetLevels:
                PostgresDataReaderProvider.AddGenericPlans(batch);
                statement = (snapToken is null ? _usersetLevelsBatchResolved : _usersetLevels)[query.Levels];
                PostgresDataReaderProvider.AddUsersetLevelsParameters(parameters, query.EntityType, query.Relation,
                    query.Ids, query.SubjectType, snapToken);
                break;
            case LookupSubjectQueryKind.Attributes:
                statement = q.SelectAttributesWithEntityIdsByEntityAttributesFilter;
                PostgresDataReaderProvider.AddAttributesWithEntityIdsParameters(parameters, query.EntityType,
                    query.Names, query.Ids, snapToken);
                break;
            case LookupSubjectQueryKind.Attribute:
                statement = q.SelectAttributesWithEntityIdsByAttributeFilter;
                PostgresDataReaderProvider.AddAttributeWithEntityIdsParameters(parameters, query.EntityType,
                    query.Names[0], query.Ids, snapToken);
                break;
            case LookupSubjectQueryKind.SubjectsExcluding:
                statement = q.GetSubjectIdsExcluding;
                PostgresDataReaderProvider.AddSubjectIdsExcludingParameters(parameters, query.SubjectType, query.Ids,
                    snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        cmd.CommandText = statement.Sql;
        statement.Bind(parameters);
        batch.BatchCommands.Add(cmd);
    }

    // Level 0 is the relation's tuples for the ids; level i is the tuples of the usersets level
    // i-1 lists, deduplicated so diamonds don't multiply rows. Plain subjects of the subject type
    // come from every level, usersets from the last one only. The snap predicate is unqualified:
    // inside a level the CTE it joins exposes only subject columns, so the transaction columns
    // resolve to the relation table.
    private static PgStatement[] UsersetLevelsStatements(ValtuutusPostgresOptions dbOptions, string snap)
    {
        var relations = PostgresDataReaderProvider.GetQueries(dbOptions).RelationsTable;
        var predicate = PostgresDataReaderProvider.SnapTokenPredicateFor(snap);
        var statements = new PgStatement[Math.Max(dbOptions.LookupHopsPerQuery, 2) + 1];
        for (var levels = 2; levels < statements.Length; levels++)
        {
            var sql = new StringBuilder();
            sql.Append($"""
                WITH l0 AS (
                    SELECT subject_type, subject_id, subject_relation FROM {relations}
                    WHERE {predicate}
                      AND entity_type = @entity_type AND relation = @relation AND entity_id = ANY(@entity_ids)
                )
                """);
            for (var i = 1; i < levels; i++)
            {
                sql.Append($"""

                    , l{i} AS (
                        SELECT DISTINCT r.subject_type, r.subject_id, r.subject_relation FROM {relations} r
                        JOIN l{i - 1} p ON r.entity_type = p.subject_type AND r.entity_id = p.subject_id
                                       AND r.relation = p.subject_relation
                        WHERE {predicate} AND p.subject_relation <> ''
                    )
                    """);
            }
            for (var i = 0; i < levels; i++)
            {
                if (i > 0) sql.Append("\nUNION ALL");
                sql.Append($"\nSELECT '' AS entity_type, '' AS entity_id, '' AS relation, subject_type, subject_id, subject_relation FROM l{i}");
                sql.Append(i < levels - 1
                    ? "\nWHERE subject_type = @subject_type AND subject_relation = ''"
                    : "\nWHERE (subject_type = @subject_type AND subject_relation = '') OR subject_relation <> ''");
            }
            statements[levels] = PgStatement.Compile(sql.ToString());
        }
        return statements;
    }
}
