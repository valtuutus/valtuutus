using System.Diagnostics.CodeAnalysis;

namespace Valtuutus.Core;

public record CheckRequest
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Permission { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectId { get; init; }
    public string? SubjectRelation { get; init; }
    
    public CheckRequest() {}
    
    [SetsRequiredMembers]
    public CheckRequest(string entityType, string entityId, string permission, string? subjectType = null, string? subjectId = null, string? subjectRelation = null)
    {
        EntityType = entityType;
        EntityId = entityId;
        Permission = permission;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectRelation = subjectRelation;
    }
}