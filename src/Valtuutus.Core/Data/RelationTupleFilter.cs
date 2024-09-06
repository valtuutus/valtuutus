using Valtuutus.Core.Engines;

namespace Valtuutus.Core.Data;

public record RelationTupleFilter : IWithSnapToken
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Relation { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectId { get; init; }
    public string? SubjectRelation { get; init; }
    public required SnapToken? SnapToken { get; set; }
}

public record EntityRelationFilter : IWithSnapToken
{
    public required string EntityType { get; init; }
    public required string Relation { get; init; }
    public required SnapToken? SnapToken { get; set; }
}