using System.Text.Json;
using System.Text.Json.Nodes;

namespace Authorizee.Core;

public record AttributeTuple
{
    public string EntityType { get; private init; } = null!;
    public string EntityId { get; private init; } = null!;
    public string Attribute { get; private init; } = null!;
    public JsonValue Value { get; private init; } = null!;
    
    protected AttributeTuple() {}
}