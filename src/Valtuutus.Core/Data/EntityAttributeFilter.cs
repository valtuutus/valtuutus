namespace Valtuutus.Core.Data;

public readonly struct EntityAttributeFilter
{
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public required string Attribute { get; init; }
    public required SnapToken SnapToken { get; init; }
}

public readonly struct EntityAttributesFilter
{
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public required string[] Attributes { get; init; }
    public required SnapToken SnapToken { get; init; }
}

public readonly struct AttributeFilter
{
    public required string EntityType { get; init; }
    public required string Attribute { get; init; }
    public required SnapToken SnapToken { get; init; }
}
