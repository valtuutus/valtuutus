namespace Valtuutus.Core.Schemas;

public class Entity
{
    public required string Name { get; init; }
    public Dictionary<string, Relation> Relations { get; init; } = new();
    public Dictionary<string, Permission> Permissions { get; init; } = new();
    public Dictionary<string, Attribute> Attributes { get; init; } = new();
}