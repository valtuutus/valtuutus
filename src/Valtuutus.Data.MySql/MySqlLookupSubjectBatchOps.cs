using System.Data.Common;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlLookupReads;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

/// <summary>
/// MySQL/MariaDB commands for the LookupSubject V2 queries: the reader's own catalog statements and
/// parameters, from the batch-resolved catalog when the batch resolves its own token, plus the
/// batch-only <see cref="LookupSubjectQueryKind.UsersetLevels"/> statements.
/// </summary>
internal sealed class MySqlLookupSubjectBatchOps(ValtuutusMySqlOptions dbOptions) : ILookupSubjectBatchOps
{
    private readonly MySqlQueries _withParameter = MySqlQueries.Get(dbOptions);
    private readonly MySqlQueries _batchResolved = MySqlQueries.GetBatchResolved(dbOptions);
    // One statement per level count, index = levels, built with the catalog.
    private readonly string[] _usersetLevels = UsersetLevelsStatements(dbOptions, MySqlQueries.SnapTokenParameter);
    private readonly string[] _usersetLevelsBatchResolved =
        UsersetLevelsStatements(dbOptions, MySqlQueries.BatchResolvedSnapToken);

    public void AddToBatch(DbBatch batch, LookupSubjectQuery query, SnapToken? snapToken)
    {
        var q = snapToken is null ? _batchResolved : _withParameter;
        var cmd = batch.CreateBatchCommand();
        var parameters = cmd.Parameters;
        cmd.CommandText = query.Kind switch
        {
            LookupSubjectQueryKind.Relations => RelationsWithEntityIds(parameters, q, query.EntityType,
                query.Relation, query.SubjectType, AsArray(query.Ids), query.SubjectRelation, snapToken),
            LookupSubjectQueryKind.RelationsMulti => RelationsWithEntityIdsMultiRelation(parameters, q,
                query.EntityType, query.Names, query.SubjectType, AsArray(query.Ids), query.SubjectRelation,
                snapToken),
            LookupSubjectQueryKind.Joined => RelationsJoinedByEntityIds(parameters, q, query.EntityType,
                query.Relation, AsArray(query.Ids), query.SubEntityType, query.SubjectRelation, query.SubRelation,
                query.SubjectType, snapToken),
            LookupSubjectQueryKind.Attributes => AttributesWithEntityIds(parameters, q, query.EntityType, query.Names,
                AsArray(query.Ids), snapToken),
            LookupSubjectQueryKind.Attribute => AttributeWithEntityIds(parameters, q, query.EntityType,
                query.Names[0], AsArray(query.Ids), snapToken),
            LookupSubjectQueryKind.SubjectsExcluding => SubjectIdsExcluding(parameters, q, query.SubjectType,
                query.Ids, snapToken),
            LookupSubjectQueryKind.UsersetLevels => UsersetLevels(parameters, query, snapToken),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Kind, null),
        };
        batch.BatchCommands.Add(cmd);
    }

    private string UsersetLevels(DbParameterCollection parameters, LookupSubjectQuery query, SnapToken? snapToken)
    {
        AddString(parameters, "@entity_type", query.EntityType, 256);
        AddString(parameters, "@relation", query.Relation, 64);
        AddJsonArray(parameters, "@entity_ids", query.Ids);
        AddString(parameters, "@subject_type", query.SubjectType, 256);
        AddSnapToken(parameters, snapToken);
        return (snapToken is null ? _usersetLevelsBatchResolved : _usersetLevels)[query.Levels];
    }

    // Level 0 is the relation's tuples for the ids; level i is the tuples of the usersets level i-1
    // lists, up to level count - 1. The recursive UNION drops a repeated (subject, level) pair, so
    // diamonds don't multiply rows, and the level cap stops a cycle. Plain subjects of the subject
    // type come from every level, usersets from the last one only. The anchor and the recursive
    // member are the only table references, whatever the level count.
    internal static string[] UsersetLevelsStatements(ValtuutusMySqlOptions dbOptions, string snap)
    {
        var rt = MySqlConnections.Relations(dbOptions);
        var statements = new string[Math.Max(dbOptions.LookupHopsPerQuery, 2) + 1];
        for (var levels = 2; levels < statements.Length; levels++)
        {
            var last = levels - 1;
            statements[levels] = Compact($"""
                WITH RECURSIVE l (subject_type, subject_id, subject_relation, lvl) AS (
                    SELECT r.subject_type, r.subject_id, r.subject_relation, 0
                    FROM {JsonIds("@entity_ids", "j")} JOIN {rt} r ON r.entity_id = j.id
                    WHERE {Snap("r", snap)} AND r.entity_type = @entity_type AND r.relation = @relation
                    UNION
                    SELECT r.subject_type, r.subject_id, r.subject_relation, l.lvl + 1
                    FROM {rt} r JOIN l ON r.entity_type = l.subject_type AND r.entity_id = l.subject_id
                                     AND r.relation = l.subject_relation
                    WHERE {Snap("r", snap)} AND l.subject_relation <> '' AND l.lvl < {last}
                )
                SELECT '' AS entity_type, '' AS entity_id, '' AS relation, subject_type, subject_id, subject_relation
                FROM l
                WHERE (subject_type = @subject_type AND subject_relation = '') OR (lvl = {last} AND subject_relation <> '')
                """);
        }
        return statements;
    }
}
