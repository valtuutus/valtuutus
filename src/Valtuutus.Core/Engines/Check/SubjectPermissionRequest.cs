using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.Check;

public record SubjectPermissionRequest
{
    private static readonly IDictionary<string, object> EmptyContext = new Dictionary<string, object>(0);

    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public SnapToken? SnapToken { get; init; }
    public int Depth { get; init; } = 10;
    public IDictionary<string, object> Context { get; init; } = EmptyContext;
};