using System.Data.Common;
using System.Text;
using Npgsql;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

/// <summary>
/// Postgres commands for the LookupEntity V2 queries: the reader's own catalog statements and
/// parameter writers, from the batch-resolved-token catalog when the batch resolves its own token,
/// plus the batch-only <see cref="LookupEntityQueryKind.RelationsLevels"/> statements.
/// </summary>
internal sealed class PostgresLookupEntityBatchOps(ValtuutusPostgresOptions dbOptions) : ILookupEntityBatchOps
{
    private readonly PostgresDataReaderProvider.ReaderQueries _withParameter =
        PostgresDataReaderProvider.GetQueries(dbOptions);
    private readonly PostgresDataReaderProvider.ReaderQueries _batchResolved =
        PostgresDataReaderProvider.GetBatchResolvedSnapTokenQueries(dbOptions);
    // One statement per level count, index = levels, built with the catalog.
    private readonly PgStatement[] _relationsLevels = RelationsLevelsStatements(dbOptions, "@snap_token");
    private readonly PgStatement[] _relationsLevelsBatchResolved =
        RelationsLevelsStatements(dbOptions, PostgresDataReaderProvider.BatchResolvedSnapTokenExpression);

    public void AddToBatch(DbBatch batch, LookupEntityQuery query, SnapToken? snapToken)
    {
        var q = snapToken is null ? _batchResolved : _withParameter;
        // Batches come from NpgsqlDataSource.CreateBatch (PostgresBatchOps), so their commands are
        // NpgsqlBatchCommands.
        var cmd = (NpgsqlBatchCommand)batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        var scoped = query.Scope is not null;
        PgStatement statement;
        switch (query.Kind)
        {
            case LookupEntityQueryKind.Relations:
            {
                var ids = AsArray(query.Ids);
                statement = PostgresDataReaderProvider.RelationsWithSubjectIdsStatement(q, scoped, ids.Length == 1);
                PostgresDataReaderProvider.AddRelationsWithSubjectIdsParameters(parameters, query.EntityType,
                    query.Relation, query.SubjectType, query.SubjectRelation, ids, query.Scope, snapToken);
                break;
            }
            case LookupEntityQueryKind.RelationsMulti:
            {
                var ids = AsArray(query.Ids);
                statement = PostgresDataReaderProvider.RelationsWithSubjectIdsMultiRelationStatement(q, scoped, ids.Length == 1);
                PostgresDataReaderProvider.AddRelationsWithSubjectIdsMultiRelationParameters(parameters,
                    query.EntityType, query.Names, query.SubjectType, query.SubjectRelation, ids, query.Scope, snapToken);
                break;
            }
            case LookupEntityQueryKind.Joined:
                statement = PostgresDataReaderProvider.RelationsJoinedStatement(q, scoped);
                PostgresDataReaderProvider.AddRelationsJoinedParameters(parameters, query.EntityType, query.Relation,
                    query.SubEntityType, query.SubjectRelation, query.SubRelation, query.SubjectType,
                    query.Ids.First(), query.Scope, snapToken);
                break;
            case LookupEntityQueryKind.Attributes:
                statement = PostgresDataReaderProvider.AttributesStatement(q, scoped);
                PostgresDataReaderProvider.AddAttributesParameters(parameters, query.EntityType, query.Names,
                    entityId: null, query.Scope, snapToken);
                break;
            case LookupEntityQueryKind.AttributesWithIds:
                statement = q.SelectAttributesWithEntityIdsByEntityAttributesFilter;
                PostgresDataReaderProvider.AddAttributesWithEntityIdsParameters(parameters, query.EntityType,
                    query.Names, query.Ids, snapToken);
                break;
            case LookupEntityQueryKind.Attribute:
                statement = q.SelectAttributesByEntityAttributeFilter;
                PostgresDataReaderProvider.AddAttributeParameters(parameters, query.EntityType, query.Names[0],
                    entityId: null, snapToken);
                break;
            case LookupEntityQueryKind.EntitiesExcluding:
                statement = q.GetEntityIdsExcluding;
                PostgresDataReaderProvider.AddEntityIdsExcludingParameters(parameters, query.EntityType, query.Ids,
                    snapToken);
                break;
            case LookupEntityQueryKind.RelationsLevels:
                AddGenericPlans(batch);
                statement =(snapToken is null ? _relationsLevelsBatchResolved : _relationsLevels)[query.Levels];
                PostgresDataReaderProvider.AddRelationsLevelsParameters(parameters, query.EntityType, query.Relation,
                    query.Ids, query.LevelOneIds, snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        cmd.CommandText = statement.Sql;
        statement.Bind(parameters);
        batch.BatchCommands.Add(cmd);
    }

    private static string[] AsArray(IReadOnlyCollection<string> ids) => ids as string[] ?? ids.ToArray();

    // Level 1 is the relation's entities whose tuples name an id through the relation, plus the
    // level-one ids; level i follows level i-1 the same way, deduplicated so diamonds don't
    // multiply rows. Each entity is reported once, at the first level reaching it, carrying the
    // relation when that level is the last. Plain equalities only: batch-resolved statements run
    // under generic plans. The snap predicate is unqualified: the level CTE a join reads exposes
    // only entity_id, so the transaction columns resolve to the relation table.
    // A levels statement's generic plan assumes id arrays of unknown size, so under the default
    // plan cache mode Postgres re-plans it on every execution; its generic plan seeks the same
    // subject index. SET LOCAL lasts for the batch's transaction and returns no rows, so the
    // batch's result sets keep one per query.
    private static void AddGenericPlans(DbBatch batch)
    {
        var cmd = batch.CreateBatchCommand();
        cmd.CommandText = "SET LOCAL plan_cache_mode = force_generic_plan";
        batch.BatchCommands.Add(cmd);
    }

    private static PgStatement[] RelationsLevelsStatements(ValtuutusPostgresOptions dbOptions, string snap)
    {
        var relations = PostgresDataReaderProvider.GetQueries(dbOptions).RelationsTable;
        var predicate = PostgresDataReaderProvider.SnapTokenPredicateFor(snap);
        var statements = new PgStatement[Math.Max(dbOptions.LookupHopsPerQuery, 2) + 1];
        for (var levels = 2; levels < statements.Length; levels++)
        {
            var sql = new StringBuilder();
            sql.Append($"""
                WITH l1 AS (
                    SELECT entity_id FROM {relations}
                    WHERE {predicate}
                      AND entity_type = @entity_type AND relation = @relation AND subject_type = @entity_type
                      AND subject_id = ANY(@ids) AND subject_relation = @relation
                    UNION
                    SELECT unnest(@level_one_ids)
                )
                """);
            for (var i = 2; i <= levels; i++)
            {
                sql.Append($"""

                    , l{i} AS (
                        SELECT DISTINCT r.entity_id FROM {relations} r
                        JOIN l{i - 1} p ON r.subject_id = p.entity_id
                        WHERE {predicate}
                          AND r.entity_type = @entity_type AND r.relation = @relation
                          AND r.subject_type = @entity_type AND r.subject_relation = @relation
                    )
                    """);
            }
            sql.Append($"""

                SELECT @entity_type AS entity_type, entity_id,
                       CASE WHEN MIN(lvl) = {levels} THEN @relation ELSE '' END AS relation,
                       '' AS subject_type, '' AS subject_id, '' AS subject_relation
                FROM (
                """);
            for (var i = 1; i <= levels; i++)
            {
                if (i > 1) sql.Append("\n    UNION ALL");
                sql.Append($"\n    SELECT entity_id, {i} AS lvl FROM l{i}");
            }
            sql.Append("\n) x\nGROUP BY entity_id");
            statements[levels] = PgStatement.Compile(sql.ToString());
        }
        return statements;
    }
}
