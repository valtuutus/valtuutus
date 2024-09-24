using System.Diagnostics.CodeAnalysis;
using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.Check;

public record CheckRequest : IWithDepth, IWithSnapToken
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Permission { get; init; }
    public string? SubjectType { get; init; }
    public string? SubjectId { get; init; }
    public string? SubjectRelation { get; init; }
    public SnapToken? SnapToken { get; set; }
    public int Depth { get; set; } = 10;
    public IDictionary<string, object> Context { get; set; } = new Dictionary<string, object>();

    public CheckRequest() { }

    [SetsRequiredMembers]
    public CheckRequest(string entityType, string entityId, string permission, string? subjectType = null,
        string? subjectId = null, string? subjectRelation = null, SnapToken? snapToken = null, IDictionary<string, object>? context = null)
    {
        EntityType = entityType;
        EntityId = entityId;
        Permission = permission;
        SubjectType = subjectType;
        SubjectId = subjectId;
        SubjectRelation = subjectRelation;
        SnapToken = snapToken;
        Context = context ?? new Dictionary<string, object>();
    }
}