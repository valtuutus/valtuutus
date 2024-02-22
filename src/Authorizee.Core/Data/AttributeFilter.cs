namespace Authorizee.Core.Data;

public class AttributeFilter
{
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public required string Attribute { get; init; }
}