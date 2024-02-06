namespace Authorizee.Core.Schemas;

public record Schema
{
    public List<Entity> Entities { get; init; }
    public List<Rule> Rules { get; init; }

    public List<ISchemaNode> SchemaNodes { get; init; } = [];
    
    public Schema(List<Entity> Entities, List<Rule> Rules)
    {
        this.Entities = Entities;
        this.Rules = Rules;
        
        SchemaNodes.AddRange(Entities.Select(x => new EntityNode
        {
            Name = x.Name
        }));

        SchemaNodes.AddRange(Entities.SelectMany(x => x.Attributes.Select(y => new AttributeNode
        {
            Name = y.Name,
            Connections =
            [
                SchemaNodes.OfType<EntityNode>()
                    .First(z => z.Name.Equals(x.Name, StringComparison.InvariantCultureIgnoreCase))
            ]
        })));

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