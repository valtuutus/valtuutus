namespace Valtuutus.Core.Data;

public readonly record struct RelationTupleFilter
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Relation { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectId { get; init; }
    public string? SubjectRelation { get; init; }
    public required SnapToken SnapToken { get; init; }
}

public readonly record struct EntityRelationFilter
{
    public required string EntityType { get; init; }
    public required string Relation { get; init; }
    public required SnapToken SnapToken { get; init; }
}
