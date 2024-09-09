using System.Runtime.CompilerServices;
using System.Text.Json;
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

    public object? GetValue(Type type)
    {
        return type switch
        {
            { } t when t == typeof(string) => Value.GetValue<string>(),
            { } t when t == typeof(int) => Value.GetValue<int>(),
            { } t when t == typeof(decimal) => Value.GetValue<decimal>(),
            { } t when t == typeof(bool) => Value.GetValue<bool>(),
            _ => throw new NotSupportedException("Unsupported type")
        };
    }
}