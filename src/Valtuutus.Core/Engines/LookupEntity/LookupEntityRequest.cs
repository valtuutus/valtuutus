using System.Diagnostics.CodeAnalysis;
using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.LookupEntity;

public record LookupEntityRequest
{
    private static readonly IDictionary<string, object> EmptyContext = new Dictionary<string, object>(0);

    [SetsRequiredMembers]
    public LookupEntityRequest(string entityType, string permission, string subjectType, string subjectId,
        int depth = 10, IDictionary<string, object>? context = null)
    {
        EntityType = entityType;
        Permission = permission;
        SubjectType = subjectType;
        SubjectId = subjectId;
        Depth = depth;
        Context = context ?? EmptyContext;
    }

    public LookupEntityRequest() { }

    public required string EntityType { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public SnapToken? SnapToken { get; init; }
    public int Depth { get; init; } = 10;
    public IDictionary<string, object> Context { get; init; } = EmptyContext;

    /// <summary>
    /// When set, constrains results to entities that have the specified relation
    /// to the given subject. All three fields of <see cref="EntityScope"/> are required.
    /// </summary>
    public EntityScope? Scope { get; init; }

    /// <summary>
    /// Maximum number of entity IDs to return. When null, all results are returned.
    /// </summary>
    public int? PageSize { get; init; }

    /// <summary>
    /// Opaque token from a previous <see cref="LookupEntityPage.ContinuationToken"/>.
    /// When set, returns results after the position indicated by the token.
    /// Pagination is positional — pass the previous page's <see cref="LookupEntityPage.SnapToken"/> as
    /// <see cref="SnapToken"/> to read every page from the same snapshot.
    /// </summary>
    public string? ContinuationToken { get; init; }
}