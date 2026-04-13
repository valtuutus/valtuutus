using System.Collections.Frozen;

namespace Valtuutus.Core.Schemas;

public class Entity
{
    public required string Name { get; init; }
    public FrozenDictionary<string, Relation> Relations { get; init; } = FrozenDictionary<string, Relation>.Empty;
    public FrozenDictionary<string, Permission> Permissions { get; init; } = FrozenDictionary<string, Permission>.Empty;
    public FrozenDictionary<string, Attribute> Attributes { get; init; } = FrozenDictionary<string, Attribute>.Empty;
}
