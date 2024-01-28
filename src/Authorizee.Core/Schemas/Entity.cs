namespace Authorizee.Core.Schemas;

public class Entity
{
    public required string Name { get; init; }
    public List<Relation> Relations { get; init; } = new();
    public List<Permission> Permissions { get; init; } = new();
}