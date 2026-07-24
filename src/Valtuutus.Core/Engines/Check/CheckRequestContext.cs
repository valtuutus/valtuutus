using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.Check;

/// <summary>
/// Request-scoped bindings for a check evaluation. Plans and ops are immutable and
/// request-independent; everything runtime-bound arrives through this context or as
/// explicit arguments.
/// </summary>
public sealed class CheckRequestContext
{
    public required string? SubjectType { get; init; }
    public required string? SubjectId { get; init; }
    public required SnapToken SnapToken { get; init; }
    public required IDictionary<string, object> Context { get; init; }
}
