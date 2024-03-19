namespace Valtuutus.Core.Data;

public record DeleteFilter
{
    public DeleteRelationsFilter[] Relations { get; init; } = Array.Empty<DeleteRelationsFilter>();

    public DeleteAttributesFilter[] Attributes { get; init; } = Array.Empty<DeleteAttributesFilter>();
}

public record struct DeleteAttributesFilter
{
    public required string EntityType { get; init; }
    
    public required string EntityId { get; init; }
    
    public string? Attribute { get; init; }
}

public record struct DeleteRelationsFilter
{
    public string? EntityType { get; init; }
    
    public string? EntityId { get; init; }
    
    public string? SubjectType { get; init; }
    
    public string? SubjectId { get; init; }
    
    public string? Relation { get; init; }
    
    public string? SubjectRelation { get; init; }
}