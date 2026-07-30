using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Valtuutus.Core.Lang;

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

    public LiteralValueUnion GetValue(Type type)
    {
        return type switch
        {
            { } t when t == typeof(string) => new LiteralValueUnion { LiteralType = LangType.String, StringValue = Value.GetValue<string>() },
            { } t when t == typeof(int) => new LiteralValueUnion { LiteralType = LangType.Int, IntValue = Value.GetValue<int>() },
            { } t when t == typeof(decimal) => new LiteralValueUnion { LiteralType = LangType.Decimal, DecimalValue = Value.GetValue<decimal>() },
            { } t when t == typeof(bool) => new LiteralValueUnion { LiteralType = LangType.Boolean, BooleanValue = Value.GetValue<bool>() },
            _ => throw new NotSupportedException("Unsupported type")
        };
    }
}
