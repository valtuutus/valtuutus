using System.Text.Json.Nodes;

namespace Valtuutus.Core;

public sealed record AttributeTuple
{
    public string EntityType { get; private init; } = null!;
    public string EntityId { get; private init; } = null!;
    public string Attribute { get; private init; } = null!;
    public JsonValue Value { get; private init; } = null!;
    
    
    public AttributeTuple(string entityType, string entityId, string attribute, JsonValue value)
    {
        EntityType = entityType;
        EntityId = entityId;
        Attribute = attribute;
        Value = value;
    }
}