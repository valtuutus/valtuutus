namespace Authorizee.Core.Schemas;

public record Schema(List<Entity> Entities, List<Rule> Rules)
{
    public List<Relation> GetRelations(string entityType)
    {
        return Entities
            .Where(e => e.Name == entityType)
            .SelectMany(x => x.Relations)
            .ToList();
    }

    public List<Permission> GetPermissions(string entityType)
    {
        return Entities
            .Where(e => e.Name == entityType)
            .SelectMany(x => x.Permissions)
            .ToList();
    }

    public List<Attribute> GetAttributes(string entityType)
    {
        return Entities.Where(e => e.Name == entityType)
            .SelectMany(e => e.Attributes)
            .ToList();
    }
}