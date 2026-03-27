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
    internal bool HasSubRelationPaths { get; init; }
}
