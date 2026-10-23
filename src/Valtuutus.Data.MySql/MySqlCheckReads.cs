using System.Data.Common;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

/// <summary>
/// The statement and parameters of each reader method a check step runs. The reader and the batch
/// ops' overrides both bind through these, so a batched check sends the same text and parameters as
/// its single-command sibling. Each method writes the parameters by name and returns the catalog
/// statement that reads them, which binds them in its placeholder order; a null token writes no snap
/// parameter, for the batch-resolved catalog.
/// </summary>
internal static class MySqlCheckReads
{
    internal static MySqlStatement GetRelations(DbParameterCollection parameters, MySqlQueries q,
        RelationTupleFilter tupleFilter)
    {
        var noSubjectFilters = string.IsNullOrWhiteSpace(tupleFilter.SubjectId)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectRelation)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectType);
        AddString(parameters, "entity_type", tupleFilter.EntityType, 256);
        AddString(parameters, "entity_id", tupleFilter.EntityId, 64);
        AddString(parameters, "relation", tupleFilter.Relation, 64);
        if (!noSubjectFilters)
        {
            AddNullableString(parameters, "subject_id", tupleFilter.SubjectId, 64);
            AddNullableString(parameters, "subject_relation", tupleFilter.SubjectRelation, 64);
            AddNullableString(parameters, "subject_type", tupleFilter.SubjectType, 256);
        }
        AddSnapToken(parameters, tupleFilter.SnapToken);
        return noSubjectFilters ? q.SelectRelationsByTupleFilterNoSubject : q.SelectRelationsByTupleFilter;
    }

    internal static MySqlStatement HasDirectRelation(DbParameterCollection parameters, MySqlQueries q,
        RelationTupleFilter tupleFilter, string subjectId)
    {
        AddString(parameters, "entity_type", tupleFilter.EntityType, 256);
        AddString(parameters, "entity_id", tupleFilter.EntityId, 64);
        AddString(parameters, "relation", tupleFilter.Relation, 64);
        AddString(parameters, "subject_id", subjectId, 64);
        AddSnapToken(parameters, tupleFilter.SnapToken);
        return q.HasDirectRelation;
    }

    internal static MySqlStatement GetIndirectRelations(DbParameterCollection parameters, MySqlQueries q,
        RelationTupleFilter tupleFilter)
    {
        AddString(parameters, "entity_type", tupleFilter.EntityType, 256);
        AddString(parameters, "entity_id", tupleFilter.EntityId, 64);
        AddString(parameters, "relation", tupleFilter.Relation, 64);
        AddSnapToken(parameters, tupleFilter.SnapToken);
        return q.GetIndirectRelations;
    }

    internal static MySqlStatement HasAnyDirectRelation(DbParameterCollection parameters, MySqlQueries q, string entityType,
        string[] entityIds, string relation, string subjectId, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddOneOrMany(parameters, "entity_id", "entity_ids", entityIds, 64);
        AddString(parameters, "relation", relation, 64);
        AddString(parameters, "subject_id", subjectId, 64);
        AddSnapToken(parameters, snapToken);
        return entityIds.Length == 1 ? q.HasDirectRelation : q.HasAnyDirectRelationMany;
    }

    internal static MySqlStatement HasAnyOfDirectRelations(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string entityId, string[] relationNames, string subjectId, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "entity_id", entityId, 64);
        AddOneOrMany(parameters, "relation", "relations", relationNames, 64);
        AddString(parameters, "subject_id", subjectId, 64);
        AddSnapToken(parameters, snapToken);
        return relationNames.Length == 1 ? q.HasAnyOfDirectRelationsOne : q.HasAnyOfDirectRelationsMany;
    }

    internal static MySqlStatement HasAnyOfAttributes(DbParameterCollection parameters, MySqlQueries q, string entityType,
        string entityId, string[] attributeNames, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "entity_id", entityId, 64);
        AddOneOrMany(parameters, "attribute", "attributes", attributeNames, 64);
        AddSnapToken(parameters, snapToken);
        return attributeNames.Length == 1 ? q.HasAnyOfAttributesOne : q.HasAnyOfAttributesMany;
    }

    internal static MySqlStatement HasTupleToUserSetRelation(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string entityId, string tupleSetRelation, string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken? snapToken)
    {
        AddSnapToken(parameters, snapToken);
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "entity_id", entityId, 64);
        AddString(parameters, "tuple_set_relation", tupleSetRelation, 64);
        AddString(parameters, "sub_entity_type", subEntityType, 256);
        AddString(parameters, "computed_relation", computedRelation, 64);
        AddString(parameters, "subject_type", subjectType, 256);
        AddString(parameters, "subject_id", subjectId, 64);
        return q.HasTupleToUserSetRelation;
    }

    internal static MySqlStatement HasUsersetJoinRelation(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string entityId, string relation, string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken? snapToken)
    {
        AddSnapToken(parameters, snapToken);
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "entity_id", entityId, 64);
        AddString(parameters, "relation", relation, 64);
        AddString(parameters, "sub_entity_type", subEntityType, 256);
        AddString(parameters, "computed_relation", computedRelation, 64);
        AddString(parameters, "subject_type", subjectType, 256);
        AddString(parameters, "subject_id", subjectId, 64);
        return q.HasUsersetJoinRelation;
    }

    internal static MySqlStatement HasTrueBoolAttribute(DbParameterCollection parameters, MySqlQueries q, string entityType,
        string entityId, string attribute, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "entity_id", entityId, 64);
        AddString(parameters, "attribute", attribute, 64);
        AddSnapToken(parameters, snapToken);
        return q.HasTrueBoolAttribute;
    }

    internal static MySqlStatement AttributesSingleEntity(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string entityId, string[] attributeNames, SnapToken? snapToken)
    {
        AddString(parameters, "entity_id", entityId, 64);
        AddString(parameters, "entity_type", entityType, 256);
        AddOneOrMany(parameters, "attribute", "attributes", attributeNames, 64);
        AddSnapToken(parameters, snapToken);
        return attributeNames.Length == 1 ? q.AttributesSingleEntityOne : q.AttributesSingleEntityMany;
    }

    // Only relation and tuple-to-userset leaves read the subject id, and only tuple-to-userset leaves
    // read the subject type, so the subject is bound only when some leaf reads it.
    internal static MySqlStatement HasFusedExpression(DbParameterCollection parameters, MySqlQueries q, string entityType,
        string entityId, IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll, string? subjectType,
        string? subjectId, SnapToken? snapToken)
    {
        var readsSubjectId = false;
        var readsSubjectType = false;
        for (var i = 0; i < leaves.Count; i++)
        {
            var kind = leaves[i].Kind;
            readsSubjectId |= kind is FusedLeafKind.Direct or FusedLeafKind.MultiDirect or FusedLeafKind.TupleToUserSet;
            readsSubjectType |= kind is FusedLeafKind.TupleToUserSet;
        }
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "entity_id", entityId, 64);
        if (readsSubjectType && subjectType is not null) AddString(parameters, "subject_type", subjectType, 256);
        if (readsSubjectId && subjectId is not null) AddString(parameters, "subject_id", subjectId, 64);
        AddSnapToken(parameters, snapToken);
        for (var i = 0; i < leaves.Count; i++)
            FusedExpressionSql.WriteLeafParameters(parameters, leaves[i], i);
        return FusedExpressionSql.BuildStatement(leaves, requireAll, q);
    }
}
