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
/// plus the batch-only <see cref="LookupEntityQueryKind.RelationsLevels"/> and
/// <see cref="LookupEntityQueryKind.RelationsLevelsFromSubject"/> statements.
/// </summary>
internal sealed class PostgresLookupEntityBatchOps(ValtuutusPostgresOptions dbOptions) : ILookupEntityBatchOps
{
    private readonly PostgresDataReaderProvider.ReaderQueries _withParameter =
        PostgresDataReaderProvider.GetQueries(dbOptions);
    private readonly PostgresDataReaderProvider.ReaderQueries _batchResolved =
        PostgresDataReaderProvider.GetBatchResolvedSnapTokenQueries(dbOptions);
    // One statement per level count, index = levels, built with the catalog.
    private readonly PgStatement[] _relationsLevels = RelationsLevelsStatements(dbOptions, "@snap_token", fromSubject: false);
    private readonly PgStatement[] _relationsLevelsBatchResolved =
        RelationsLevelsStatements(dbOptions, PostgresDataReaderProvider.BatchResolvedSnapTokenExpression, fromSubject: false);
    private readonly PgStatement[] _relationsLevelsFromSubject =
        RelationsLevelsStatements(dbOptions, "@snap_token", fromSubject: true);
    private readonly PgStatement[] _relationsLevelsFromSubjectBatchResolved =
        RelationsLevelsStatements(dbOptions, PostgresDataReaderProvider.BatchResolvedSnapTokenExpression, fromSubject: true);

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
                PostgresDataReaderProvider.AddGenericPlans(batch);
                statement = (snapToken is null ? _relationsLevelsBatchResolved : _relationsLevels)[query.Levels];
                PostgresDataReaderProvider.AddRelationsLevelsParameters(parameters, query.EntityType, query.Relation,
                    query.Ids, query.LevelOneIds, snapToken);
                break;
            case LookupEntityQueryKind.RelationsLevelsFromSubject:
                PostgresDataReaderProvider.AddGenericPlans(batch);
                statement = (snapToken is null ? _relationsLevelsFromSubjectBatchResolved : _relationsLevelsFromSubject)[query.Levels];
                PostgresDataReaderProvider.AddRelationsLevelsFromSubjectParameters(parameters, query.EntityType,
                    query.Relation, query.SubjectType, query.Ids.First(), snapToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null);
        }
        cmd.CommandText = statement.Sql;
        statement.Bind(parameters);
        batch.BatchCommands.Add(cmd);
    }

    private static string[] AsArray(IReadOnlyCollection<string> ids) => ids as string[] ?? ids.ToArray();

    // From ids, level 1 is the relation's entities whose tuples name an id through the relation,
    // plus the level-one ids. From the subject, level 0 is the relation's entities whose tuples name
    // the direct subject, and level 1 follows it. Level i follows level i-1 the same way, up to the
    // level count. The recursive UNION drops a repeated (entity, level) pair, so diamonds don't
    // multiply rows, and the level cap stops a cycle. Each entity is reported once, at the first
    // level reaching it, carrying the relation when that level is the last. The anchor and the
    // recursive member are the only table references, whatever the level count. Ids are text in
    // both terms, since a recursive CTE's column types must match exactly and the level-one ids
    // arrive as a text array. Plain equalities only: the statements run under generic plans.
    private static PgStatement[] RelationsLevelsStatements(ValtuutusPostgresOptions dbOptions, string snap,
        bool fromSubject)
    {
        var relations = PostgresDataReaderProvider.GetQueries(dbOptions).RelationsTable;
        var predicate = PostgresDataReaderProvider.SnapTokenPredicateFor(snap, "r");
        var anchor = fromSubject
            ? $"""
                SELECT r.entity_id::text, 0 FROM {relations} r
                    WHERE {predicate}
                      AND r.entity_type = @entity_type AND r.relation = @relation AND r.subject_type = @subject_type
                      AND r.subject_id = @subject_id AND r.subject_relation = ''
                """
            : $"""
                SELECT r.entity_id::text, 1 FROM {relations} r
                    WHERE {predicate}
                      AND r.entity_type = @entity_type AND r.relation = @relation AND r.subject_type = @entity_type
                      AND r.subject_id = ANY(@ids) AND r.subject_relation = @relation
                    UNION
                    SELECT unnest(@level_one_ids), 1
                """;
        var statements = new PgStatement[Math.Max(dbOptions.LookupHopsPerQuery, 2) + 1];
        for (var levels = fromSubject ? 1 : 2; levels < statements.Length; levels++)
        {
            statements[levels] = PgStatement.Compile($"""
                WITH RECURSIVE l (entity_id, lvl) AS (
                    {anchor}
                    UNION
                    SELECT r.entity_id::text, l.lvl + 1 FROM {relations} r
                    JOIN l ON r.subject_id = l.entity_id
                    WHERE {predicate}
                      AND r.entity_type = @entity_type AND r.relation = @relation
                      AND r.subject_type = @entity_type AND r.subject_relation = @relation AND l.lvl < {levels}
                )
                SELECT @entity_type AS entity_type, entity_id,
                       CASE WHEN MIN(lvl) = {levels} THEN @relation ELSE '' END AS relation,
                       '' AS subject_type, '' AS subject_id, '' AS subject_relation
                FROM l
                GROUP BY entity_id
                """);
        }
        return statements;
    }
}
