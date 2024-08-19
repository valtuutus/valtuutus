using System.Diagnostics.CodeAnalysis;

namespace Valtuutus.Core;


public record LookupEntityRequest
{
    [SetsRequiredMembers]
    public LookupEntityRequest(string entityType, string permission, string subjectType, string subjectId, int depth = 10)
    {
        EntityType = entityType;
        Permission = permission;
        SubjectType = subjectType;
        SubjectId = subjectId;
        Depth = depth;
    }

    public LookupEntityRequest() {}
    
    public required string EntityType { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public int Depth { get; set; }
}