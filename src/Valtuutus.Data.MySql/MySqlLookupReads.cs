using System.Data.Common;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

/// <summary>
/// The statement and parameters of each reader method a lookup step can also run as a batch
/// command. The reader and the lookup batch ops both bind through these, so a batched query sends
/// the same text and parameters as its single-command sibling. Each method writes the parameters by
/// name and returns the catalog statement that reads them, which binds them in its placeholder
/// order; a null token writes no snap parameter, for the batch-resolved catalog.
/// </summary>
internal static class MySqlLookupReads
{
    internal static void AddScope(DbParameterCollection parameters, EntityScope? scope)
    {
        if (scope is not { } s)
            return;
        AddString(parameters, "scope_relation", s.Relation, 64);
        AddString(parameters, "scope_subject_type", s.SubjectType, 256);
        AddString(parameters, "scope_subject_id", s.SubjectId, 64);
    }

    internal static MySqlStatement RelationsWithSubjectIds(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string relation, IReadOnlyCollection<string> subjectIds, string subjectType, string subjectRelation,
        EntityScope? scope, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "relation", relation, 64);
        AddString(parameters, "subject_type", subjectType, 256);
        AddString(parameters, "subject_relation", subjectRelation, 64);
        AddOneOrMany(parameters, "subject_id", "subject_ids", subjectIds, 64);
        AddScope(parameters, scope);
        AddSnapToken(parameters, snapToken);
        var single = subjectIds.Count == 1;
        return scope is not null
            ? (single ? q.RelationsWithSubjectOneScoped : q.RelationsWithSubjectManyScoped)
            : (single ? q.RelationsWithSubjectOne : q.RelationsWithSubjectMany);
    }

    internal static MySqlStatement RelationsWithSubjectIdsMultiRelation(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string[] relationNames, IReadOnlyCollection<string> subjectIds, string subjectType, string subjectRelation,
        EntityScope? scope, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddJsonArray(parameters, "relations", relationNames);
        AddString(parameters, "subject_type", subjectType, 256);
        AddString(parameters, "subject_relation", subjectRelation, 64);
        AddOneOrMany(parameters, "subject_id", "subject_ids", subjectIds, 64);
        AddScope(parameters, scope);
        AddSnapToken(parameters, snapToken);
        var single = subjectIds.Count == 1;
        return scope is not null
            ? (single ? q.RelationsWithSubjectOneMultiRelationScoped : q.RelationsWithSubjectManyMultiRelationScoped)
            : (single ? q.RelationsWithSubjectOneMultiRelation : q.RelationsWithSubjectManyMultiRelation);
    }

    internal static MySqlStatement RelationsJoined(DbParameterCollection parameters, MySqlQueries q, string entityType,
        string relation, string subEntityType, string subjectRelation, string subRelation, string subjectType,
        string subjectId, EntityScope? scope, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "relation", relation, 64);
        AddString(parameters, "sub_entity_type", subEntityType, 256);
        AddString(parameters, "subject_relation", subjectRelation, 64);
        AddString(parameters, "sub_relation", subRelation, 64);
        AddString(parameters, "subject_type", subjectType, 256);
        AddString(parameters, "subject_id", subjectId, 64);
        AddScope(parameters, scope);
        AddSnapToken(parameters, snapToken);
        return scope is not null ? q.JoinedScoped : q.Joined;
    }

    internal static MySqlStatement RelationsWithEntityIds(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string relation, string subjectType, IReadOnlyCollection<string> entityIds, string subjectRelation,
        SnapToken? snapToken)
    {
        AddString(parameters, "subject_type", subjectType, 256);
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "relation", relation, 64);
        AddOneOrMany(parameters, "entity_id", "entity_ids", entityIds, 64);
        AddString(parameters, "subject_relation", subjectRelation, 64);
        AddSnapToken(parameters, snapToken);
        return entityIds.Count == 1 ? q.RelationsWithEntityOne : q.RelationsWithEntityMany;
    }

    internal static MySqlStatement RelationsWithEntityIdsMultiRelation(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string[] relationNames, string subjectType, IReadOnlyCollection<string> entityIds, string subjectRelation,
        SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddJsonArray(parameters, "relations", relationNames);
        AddString(parameters, "subject_type", subjectType, 256);
        AddOneOrMany(parameters, "entity_id", "entity_ids", entityIds, 64);
        AddString(parameters, "subject_relation", subjectRelation, 64);
        AddSnapToken(parameters, snapToken);
        return entityIds.Count == 1 ? q.RelationsWithEntityOneMultiRelation : q.RelationsWithEntityManyMultiRelation;
    }

    internal static MySqlStatement RelationsJoinedByEntityIds(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string relation, IReadOnlyCollection<string> entityIds, string subEntityType, string subjectRelation,
        string subRelation, string subjectType, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "relation", relation, 64);
        AddOneOrMany(parameters, "entity_id", "entity_ids", entityIds, 64);
        AddString(parameters, "sub_entity_type", subEntityType, 256);
        AddString(parameters, "subject_relation", subjectRelation, 64);
        AddString(parameters, "sub_relation", subRelation, 64);
        AddString(parameters, "subject_type", subjectType, 256);
        AddSnapToken(parameters, snapToken);
        return entityIds.Count == 1 ? q.JoinedByEntityIdsOne : q.JoinedByEntityIdsMany;
    }

    // A null entity id reads every entity of the type. The scoped statement has no entity id filter;
    // it reads its scope parameters instead.
    internal static MySqlStatement AttributesByAttributes(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string[] attributes, string? entityId, EntityScope? scope, SnapToken? snapToken)
    {
        if (scope is null)
            AddNullableString(parameters, "entity_id", entityId, 64);
        AddString(parameters, "entity_type", entityType, 256);
        AddOneOrMany(parameters, "attribute", "attributes", attributes, 64);
        AddScope(parameters, scope);
        AddSnapToken(parameters, snapToken);
        var single = attributes.Length == 1;
        return scope is not null
            ? (single ? q.AttributesScopedOne : q.AttributesScopedMany)
            : (single ? q.AttributesByAttributesOne : q.AttributesByAttributesMany);
    }

    // A null entity id reads every entity of the type.
    internal static MySqlStatement AttributesByAttribute(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string attribute, string? entityId, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "attribute", attribute, 64);
        AddNullableString(parameters, "entity_id", entityId, 64);
        AddSnapToken(parameters, snapToken);
        return q.AttributesByAttribute;
    }

    internal static MySqlStatement AttributeWithEntityIds(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string attribute, IReadOnlyCollection<string> entityIds, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddString(parameters, "attribute", attribute, 64);
        AddOneOrMany(parameters, "entity_id", "entity_ids", entityIds, 64);
        AddSnapToken(parameters, snapToken);
        return entityIds.Count == 1 ? q.AttributesWithEntityIdsOne : q.AttributesWithEntityIdsMany;
    }

    internal static MySqlStatement AttributesWithEntityIds(DbParameterCollection parameters, MySqlQueries q,
        string entityType, string[] attributes, IReadOnlyCollection<string> entityIds, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddJsonArray(parameters, "attributes", attributes);
        AddOneOrMany(parameters, "entity_id", "entity_ids", entityIds, 64);
        AddSnapToken(parameters, snapToken);
        return entityIds.Count == 1
            ? q.AttributesWithEntityIdsMultiAttributeOne
            : q.AttributesWithEntityIdsMultiAttributeMany;
    }

    internal static MySqlStatement EntityIdsExcluding(DbParameterCollection parameters, MySqlQueries q, string entityType,
        IEnumerable<string> excludeIds, SnapToken? snapToken)
    {
        AddString(parameters, "entity_type", entityType, 256);
        AddJsonArray(parameters, "exclude_ids", excludeIds);
        AddSnapToken(parameters, snapToken);
        return q.EntityIdsExcluding;
    }

    internal static MySqlStatement SubjectIdsExcluding(DbParameterCollection parameters, MySqlQueries q, string subjectType,
        IEnumerable<string> excludeIds, SnapToken? snapToken)
    {
        AddString(parameters, "subject_type", subjectType, 256);
        AddJsonArray(parameters, "exclude_ids", excludeIds);
        AddSnapToken(parameters, snapToken);
        return q.SubjectIdsExcluding;
    }
}
