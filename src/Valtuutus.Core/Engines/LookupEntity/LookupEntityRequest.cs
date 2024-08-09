using System.Diagnostics.CodeAnalysis;
using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.LookupEntity;


public record LookupEntityRequest
{
    [SetsRequiredMembers]
    public LookupEntityRequest(string entityType, string permission, string subjectType, string subjectId)
    {
        EntityType = entityType;
        Permission = permission;
        SubjectType = subjectType;
        SubjectId = subjectId;
    }

    public LookupEntityRequest() {}
    
    public required string EntityType { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public SnapToken? SnapToken { get; set; }
    
}