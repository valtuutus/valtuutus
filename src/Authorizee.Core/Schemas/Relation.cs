namespace Authorizee.Core.Schemas;

public record RelationEntity
{
    public required string Type { get; init; }
    public string? Relation { get; init; }
}

public record Relation
{
    public required string Name { get; init; }
    public required List<RelationEntity> Entities { get; init; }
}
