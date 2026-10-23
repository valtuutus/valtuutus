using System.Data.Common;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlLookupReads;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

/// <summary>
/// MySQL/MariaDB commands for the LookupEntity V2 queries: the reader's own catalog statements and
/// parameters, from the batch-resolved catalog when the batch resolves its own token, plus the
/// batch-only <see cref="LookupEntityQueryKind.RelationsLevels"/> and
/// <see cref="LookupEntityQueryKind.RelationsLevelsFromSubject"/> statements.
/// </summary>
internal sealed class MySqlLookupEntityBatchOps(ValtuutusMySqlOptions dbOptions) : ILookupEntityBatchOps
{
    private readonly MySqlQueries _withParameter = MySqlQueries.Get(dbOptions);
    private readonly MySqlQueries _batchResolved = MySqlQueries.GetBatchResolved(dbOptions);
    // One statement per level count, index = levels, built with the catalog.
    private readonly string[] _relationsLevels =
        RelationsLevelsStatements(dbOptions, MySqlQueries.SnapTokenParameter, fromSubject: false);
    private readonly string[] _relationsLevelsBatchResolved =
        RelationsLevelsStatements(dbOptions, MySqlQueries.BatchResolvedSnapToken, fromSubject: false);
    private readonly string[] _relationsLevelsFromSubject =
        RelationsLevelsStatements(dbOptions, MySqlQueries.SnapTokenParameter, fromSubject: true);
    private readonly string[] _relationsLevelsFromSubjectBatchResolved =
        RelationsLevelsStatements(dbOptions, MySqlQueries.BatchResolvedSnapToken, fromSubject: true);

    public void AddToBatch(DbBatch batch, LookupEntityQuery query, SnapToken? snapToken)
    {
        var q = snapToken is null ? _batchResolved : _withParameter;
        var cmd = batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        cmd.CommandText = query.Kind switch
        {
            LookupEntityQueryKind.Relations => RelationsWithSubjectIds(parameters, q, query.EntityType,
                query.Relation, AsArray(query.Ids), query.SubjectType, query.SubjectRelation, query.Scope, snapToken),
            LookupEntityQueryKind.RelationsMulti => RelationsWithSubjectIdsMultiRelation(parameters, q,
                query.EntityType, query.Names, AsArray(query.Ids), query.SubjectType, query.SubjectRelation,
                query.Scope, snapToken),
            LookupEntityQueryKind.Joined => RelationsJoined(parameters, q, query.EntityType, query.Relation,
                query.SubEntityType, query.SubjectRelation, query.SubRelation, query.SubjectType, query.Ids.First(),
                query.Scope, snapToken),
            LookupEntityQueryKind.Attributes => AttributesByAttributes(parameters, q, query.EntityType, query.Names,
                entityId: null, query.Scope, snapToken),
            LookupEntityQueryKind.AttributesWithIds => AttributesWithEntityIds(parameters, q, query.EntityType,
                query.Names, AsArray(query.Ids), snapToken),
            LookupEntityQueryKind.Attribute => AttributesByAttribute(parameters, q, query.EntityType, query.Names[0],
                entityId: null, snapToken),
            LookupEntityQueryKind.EntitiesExcluding => EntityIdsExcluding(parameters, q, query.EntityType, query.Ids,
                snapToken),
            LookupEntityQueryKind.RelationsLevels => RelationsLevels(parameters, query, snapToken),
            LookupEntityQueryKind.RelationsLevelsFromSubject => RelationsLevelsFromSubject(parameters, query,
                snapToken),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null),
        };
        batch.BatchCommands.Add(cmd);
    }

    private string RelationsLevels(DbParameterCollection parameters, LookupEntityQuery query, SnapToken? snapToken)
    {
        AddString(parameters, "@entity_type", query.EntityType, 256);
        AddString(parameters, "@relation", query.Relation, 64);
        AddJsonArray(parameters, "@subject_ids", query.Ids);
        AddJsonArray(parameters, "@level_one_ids", query.LevelOneIds);
        AddSnapToken(parameters, snapToken);
        return (snapToken is null ? _relationsLevelsBatchResolved : _relationsLevels)[query.Levels];
    }

    private string RelationsLevelsFromSubject(DbParameterCollection parameters, LookupEntityQuery query,
        SnapToken? snapToken)
    {
        AddString(parameters, "@entity_type", query.EntityType, 256);
        AddString(parameters, "@relation", query.Relation, 64);
        AddString(parameters, "@subject_type", query.SubjectType, 256);
        AddString(parameters, "@subject_id", query.Ids.First(), 64);
        AddSnapToken(parameters, snapToken);
        return (snapToken is null ? _relationsLevelsFromSubjectBatchResolved : _relationsLevelsFromSubject)[query.Levels];
    }

    // From ids, level 1 is the relation's entities whose tuples name an id through the relation,
    // plus the level-one ids. From the subject, level 0 is the relation's entities whose tuples name
    // the direct subject, and level 1 follows it. Level i follows level i-1 the same way, up to the
    // level count. The recursive UNION drops a repeated (entity, level) pair, so diamonds don't
    // multiply rows, and the level cap stops a cycle. Each entity is reported once, at the first
    // level reaching it, carrying the relation when that level is the last. The anchor and the
    // recursive member are the only table references, whatever the level count.
    internal static string[] RelationsLevelsStatements(ValtuutusMySqlOptions dbOptions, string snap, bool fromSubject)
    {
        var rt = MySqlConnections.Relations(dbOptions);
        var anchor = fromSubject
            ? $"""
                SELECT r.entity_id, 0 FROM {rt} r
                WHERE {Snap("r", snap)} AND r.entity_type = @entity_type AND r.relation = @relation
                  AND r.subject_type = @subject_type AND r.subject_id = @subject_id AND r.subject_relation = ''
                """
            : $"""
                SELECT r.entity_id, 1 FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                WHERE {Snap("r", snap)} AND r.entity_type = @entity_type AND r.relation = @relation
                  AND r.subject_type = @entity_type AND r.subject_relation = @relation
                UNION
                SELECT o.id, 1 FROM {JsonIds("@level_one_ids", "o")}
                """;
        var statements = new string[Math.Max(dbOptions.LookupHopsPerQuery, 2) + 1];
        for (var levels = fromSubject ? 1 : 2; levels < statements.Length; levels++)
        {
            statements[levels] = Compact($"""
                WITH RECURSIVE l (entity_id, lvl) AS (
                    {anchor}
                    UNION
                    SELECT r.entity_id, l.lvl + 1 FROM {rt} r JOIN l ON r.subject_id = l.entity_id
                    WHERE {Snap("r", snap)} AND r.entity_type = @entity_type AND r.relation = @relation
                      AND r.subject_type = @entity_type AND r.subject_relation = @relation AND l.lvl < {levels}
                )
                SELECT @entity_type AS entity_type, entity_id,
                       CASE WHEN MIN(lvl) = {levels} THEN @relation ELSE '' END AS relation,
                       '' AS subject_type, '' AS subject_id, '' AS subject_relation
                FROM l GROUP BY entity_id
                """);
        }
        return statements;
    }
}
