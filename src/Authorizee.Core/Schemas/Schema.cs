namespace Authorizee.Core.Schemas;

public record Schema
{
    public List<Entity> Entities { get; init; }
    public List<Rule> Rules { get; init; }

    private Dictionary<string, Dictionary<string, Relation>> _entitiesRelations;
    private Dictionary<string, Dictionary<string, Relation>> _entitiesPermissions;
    
    
    private Dictionary<string, Dictionary<string, Relation>> _permissionLinks;
    
    public Schema(List<Entity> Entities, List<Rule> Rules)
    {
        this.Entities = Entities;
        this.Rules = Rules;
        
        
    }

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
    
    public void Deconstruct(out List<Entity> Entities, out List<Rule> Rules)
    {
        Entities = this.Entities;
        Rules = this.Rules;
    }
}