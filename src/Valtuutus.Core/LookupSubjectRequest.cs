using System.Diagnostics.CodeAnalysis;

namespace Valtuutus.Core;

public record LookupSubjectRequest
{
    [SetsRequiredMembers]
    public LookupSubjectRequest(string entityType, string permission, string subjectType, string entityId, int depth = 10)
    {
        EntityType = entityType;
        Permission = permission;
        SubjectType = subjectType;
        EntityId = entityId;
        Depth = depth;
    }

    public LookupSubjectRequest() {}
    
    
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public int Depth { get; set; }
}