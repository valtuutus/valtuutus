namespace Valtuutus.Core.Schemas;

public record RelationEntity
{
    public required string Type { get; init; }
    public string? Relation { get; init; }
}

public record Relation
{
    public required string Name { get; init; }
    public required List<RelationEntity> Entities { get; init; }
    internal HashSet<string> EntityTypes { get; init; } = [];

    /// <summary>True when at least one target in <see cref="Entities"/> is a userset reference
    /// (<c>@entity#relation</c>) rather than a plain entity type — a direct check on this relation
    /// can't fast-path to a single round trip without expanding the sub-relation.</summary>
    public bool HasSubRelationPaths { get; init; }
}
