namespace Authorizee.Core.Schemas;

public interface ISchemaNode
{
    public NodeType Type { get; }
    public List<ISchemaNode> Connections { get; init; }
}

public record SchemaGraph
{

    public SchemaGraph(Schema schema)
    {
                
        Nodes.AddRange(schema.Entities.Select(x => new EntityNode
        {
            Name = x.Name
        }));
        
        Nodes.AddRange(schema.Entities.SelectMany(x => x.Relations).Select(x => new RelationNode()
        {
            Name = x.Name,
        }));
        
        Nodes.AddRange(schema.Entities.SelectMany(x => x.Attributes).Select(x => new AttributeNode()
        {
            Name = x.Name
        }));
    }
    
    
    public List<ISchemaNode> Nodes { get; init; } = [];

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
    public required PermissionNode ExpressionRoot { get; init; }
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
