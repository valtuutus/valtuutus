using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Core.Schemas;

public record Schema(Dictionary<string, Entity> Entities, Dictionary<string, Function> Functions)
{
    internal RelationType GetRelationType(string entityType, string permission)
    {
        var found = Entities.TryGetValue(entityType, out var entity);
        if (!found) return RelationType.None;
        if (entity!.Attributes.ContainsKey(permission)) return RelationType.Attribute;
        if (entity.Relations.ContainsKey(permission)) return RelationType.DirectRelation;
        if (entity.Permissions.ContainsKey(permission)) return RelationType.Permission;
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

    internal List<Permission> GetPermissions(string entityType)
    {
        return Entities[entityType].Permissions.Select(x => x.Value)
            .ToList();
    }
}