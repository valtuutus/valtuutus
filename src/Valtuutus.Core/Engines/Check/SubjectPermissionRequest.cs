using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.Check;

public record SubjectPermissionRequest : IWithSnapToken
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public SnapToken? SnapToken { get; set; }
    public int Depth { get; init; } = 10;
};