namespace Authorizee.Core.Data;

public record RelationTupleFilter
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Relation { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectId { get; init; }
    public string? SubjectRelation { get; init; }
}

public record EntityRelationFilter
{
    public required string EntityType { get; init; }
    public required string Relation { get; init; }
}