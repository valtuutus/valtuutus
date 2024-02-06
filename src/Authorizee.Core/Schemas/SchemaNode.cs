namespace Authorizee.Core.Schemas;

public interface ISchemaNode
{
    public NodeType Type { get; }
    public List<ISchemaNode> Connections { get; init; }
}

public record SchemaGraph
{
    private readonly Schema _schema;
    private readonly List<(string, string)> _relations = new();
    private readonly Dictionary<string, ISchemaNode> _nodeKeyMap = new();

    public SchemaGraph(Schema schema)
    {
        _schema = schema;
        foreach (var entity in schema.Entities)
        {
            BuildGraphRecursive(BuildEntityKey(entity.Name), null);
        }

        foreach (var (r1, r2) in _relations)
        {
            Console.WriteLine($"{r1} ---> {r2}");
        }
    }

    private void BuildGraphRecursive(string key, string? parentKey)
    {
        if (!string.IsNullOrWhiteSpace(parentKey))
        {
            _relations.Add((parentKey, key));
            _relations.Add((key, parentKey));
        }
        
        if (_nodeKeyMap.ContainsKey(key))
            return;
        
        Action builder = key.Split(':') switch
        {
            ["entity", { } entityName] => () => BuildEntityGraphRecursive(key, entityName),
            ["entity", { } entityName, "attr", { } attrName] => () => BuildAttributeGraphRecursive(key, attrName, entityName),
            ["entity", { } entityName, "relation", { } relationName] => () => BuildRelationGraphRecursive(key, relationName, entityName),
        };

        builder();
    }

    public void BuildEntityGraphRecursive(string key, string entityName)
    {
        var entity =
            _schema.Entities.First(x => x.Name.Equals(entityName, StringComparison.InvariantCultureIgnoreCase));
        
        _nodeKeyMap.Add(key, new EntityNode()
        {
            Name = entity.Name
        });
        
        foreach (var attr in entity.Attributes)
        {
            BuildGraphRecursive(BuildAttrKey(entityName, attr.Name), key);
        }
        
        foreach (var relation in entity.Relations)
        {
            BuildGraphRecursive(BuildRelationKey(entityName, relation.Name), key);
        }
    }
    
    public void BuildPermissionGraphRecursive(string key)
    {
        
    }
    
    public void BuildRelationGraphRecursive(string key, string relationName, string entityName)
    {
        var relation = _schema.GetRelations(entityName)
            .First(x => x.Name.Equals(relationName, StringComparison.InvariantCultureIgnoreCase));
        
        _nodeKeyMap.Add(key, new RelationNode()
        {
            Name = relationName
        });

        foreach (var relationEntity in relation.Entities)
        {
            if (!string.IsNullOrWhiteSpace(relationEntity.Relation))
            {
                BuildGraphRecursive(BuildRelationKey(relationEntity.Type, relationEntity.Relation), key);
            }
            else
            {
                BuildGraphRecursive(BuildEntityKey(relationEntity.Type), key);
            }
        }
    }
    
    public void BuildAttributeGraphRecursive(string key, string attributeName, string entityName)
    {
        var attr = _schema.GetAttributes(entityName)
            .First(x => x.Name.Equals(attributeName, StringComparison.InvariantCultureIgnoreCase));
        
        _nodeKeyMap.Add(key, new AttributeNode()
        {
            Name = attr.Name
        });
    }
    
    private static string BuildAttrKey(string entityName, string attributeName)
    {
        return $"entity:{entityName}:attr:{attributeName}";
    }
    
    private static string BuildEntityKey(string entityName)
    {
        return $"entity:{entityName}";
    }
    
    private static string BuildRelationKey(string entityName, string relationName)
    {
        return $"entity:{entityName}:relation:{relationName}";
    }

}

public record EntityNode : ISchemaNode
{
    public NodeType Type => NodeType.Entity;
    public required string Name { get; init; }
    public List<ISchemaNode> Connections { get; init; } = [];

}

public record AttributeNode : ISchemaNode
{
    public required string Name { get; init; }
    public NodeType Type => NodeType.Attribute;
    public List<ISchemaNode> Connections { get; init; } = [];
}

public record RelationNode : ISchemaNode
{
    public required string Name { get; init; }
    public NodeType Type => NodeType.Relation;
    public List<ISchemaNode> Connections { get; init; } = [];
}

public record PermissionGraphNode : ISchemaNode
{
    public required string Name { get; init; }
    // public required PermissionNode ExpressionRoot { get; init; }
    public NodeType Type => NodeType.ExpressionOperator;
    public List<ISchemaNode> Connections { get; init; } = [];
}

public enum NodeType
{
    Entity,
    Attribute,
    Relation,
    ExpressionOperator,
    Rule
}
