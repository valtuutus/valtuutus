using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Core.Schemas;

public record Schema(Dictionary<string, Entity> Entities, Dictionary<string, Function> Functions)
{
    internal RelationType GetRelationType(string entityType, string permission)
    {
        var found = Entities.TryGetValue(entityType, out var entity);
        if (!found) return RelationType.None;
        if (entity!.Permissions.ContainsKey(permission)) return RelationType.Permission;
        if (entity.Relations.ContainsKey(permission)) return RelationType.DirectRelation;
        if (entity.Attributes.ContainsKey(permission)) return RelationType.Attribute;
        return RelationType.None;
    }

    internal Relation GetRelation(string entityType, string relation)
    {
        return Entities[entityType].Relations[relation];
    }
    
    internal Permission GetPermission(string entityType, string permission)
    {
        return Entities[entityType].Permissions[permission];

    }
    
    internal Attribute GetAttribute(string entityType, string attribute)
    {
        return Entities[entityType].Attributes[attribute];
    }

    internal Dictionary<string, Permission>.ValueCollection GetPermissions(string entityType)
    {
        return Entities[entityType].Permissions.Values;
    }

    /// <summary>
    /// Returns true when <paramref name="subjectType"/> (with optional <paramref name="subjectRelation"/>)
    /// is listed as an allowed subject type for <c>entityType.relation</c> in the schema.
    /// Call before any DB round-trip to short-circuit checks that are impossible by schema.
    /// </summary>
    internal bool IsSubjectTypeAllowedInRelation(string entityType, string relation,
        string subjectType, string? subjectRelation)
    {
        var rel = GetRelation(entityType, relation);
        var subRel = subjectRelation ?? string.Empty;
        foreach (var entity in rel.Entities)
        {
            if (entity.Type == subjectType && (entity.Relation ?? string.Empty) == subRel)
                return true;
        }
        return false;
    }
}