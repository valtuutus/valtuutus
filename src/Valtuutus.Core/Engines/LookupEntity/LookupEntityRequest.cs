using System.Diagnostics.CodeAnalysis;
using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.LookupEntity;

public record LookupEntityRequest : IWithDepth, IWithSnapToken
{
    [SetsRequiredMembers]
    public LookupEntityRequest(string entityType, string permission, string subjectType, string subjectId,
        int depth = 10, IDictionary<string, object>? context = null)
    {
        EntityType = entityType;
        Permission = permission;
        SubjectType = subjectType;
        SubjectId = subjectId;
        Depth = depth;
        Context = context ?? new Dictionary<string, object>();
    }

    public LookupEntityRequest() { }

    public required string EntityType { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public SnapToken? SnapToken { get; set; }
    public int Depth { get; set; }
    public IDictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
}