namespace Valtuutus.Core.Data;

public class EntityAttributeFilter
{
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public required string Attribute { get; init; }
    public required SnapToken? SnapToken { get; set; }
}

public class EntityAttributesFilter
{
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public required string[] Attributes { get; init; }
    public required SnapToken? SnapToken { get; set; }
}

public class AttributeFilter
{
    public required string EntityType { get; init; }
    public required string Attribute { get; init; }
    public SnapToken? SnapToken { get; set; }
}