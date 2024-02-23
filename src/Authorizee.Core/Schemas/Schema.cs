namespace Authorizee.Core.Schemas;

public record Schema(Dictionary<string, Entity> Entities)
{
    public RelationType GetRelationType(string entityType, string permission)
    {
        var found = Entities.TryGetValue(entityType, out var entity);
        if (!found) return RelationType.None;
        if (entity!.Attributes.ContainsKey(permission)) return RelationType.Attribute;
        if (entity.Relations.ContainsKey(permission)) return RelationType.DirectRelation;
        if (entity.Permissions.ContainsKey(permission)) return RelationType.Permission;
        return RelationType.None;
    }

    public Relation GetRelation(string entityType, string relation)
    {
        return Entities[entityType].Relations[relation];
    }
    
    public Permission GetPermission(string entityType, string permission)
    {
        return Entities[entityType].Permissions[permission];

    }
    
    public Attribute GetAttribute(string entityType, string attribute)
    {
        return Entities[entityType].Attributes[attribute];
    }
    
    public List<Relation> GetRelations(string entityType)
    {
        return Entities[entityType].Relations.Select(x => x.Value)
            .ToList();
    }

    public List<Permission> GetPermissions(string entityType)
    {
        return Entities[entityType].Permissions.Select(x => x.Value)
            .ToList();
    }
}