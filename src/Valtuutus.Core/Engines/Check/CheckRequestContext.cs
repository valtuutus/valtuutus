using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.Check;

internal sealed class CheckRequestContext
{
    public required string? SubjectType { get; init; }
    public required string? SubjectId { get; init; }
    public required SnapToken SnapToken { get; init; }
    public required IDictionary<string, object> Context { get; init; }
}
