namespace Authorizee.Core;

public record SubjectPermissionRequest
{
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
};