using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.Check;

/// <summary>
/// Request-scoped bindings for a check evaluation. Plans and ops are immutable and
/// request-independent; everything runtime-bound arrives through this context or as
/// explicit arguments.
/// </summary>
public sealed class CheckRequestContext
{
    private readonly SnapToken _snapToken;

    public required string? SubjectType { get; init; }
    public required string? SubjectId { get; init; }

    /// <summary>The token every query of the request runs against. Not available while
    /// <see cref="ResolveLatestSnapToken"/> is set — reading it then throws.</summary>
    public required SnapToken SnapToken
    {
        get => ResolveLatestSnapToken
            ? throw new InvalidOperationException(
                "The snap token is resolved by the physical executor; read it from the context the executor resolved.")
            : _snapToken;
        init => _snapToken = value;
    }

    public required IDictionary<string, object> Context { get; init; }

    /// <summary>
    /// Set when the caller supplied no token and the physical executor declared
    /// <see cref="V2.IPhysicalExecutor.CanResolveSnapTokenInFirstRoundTrip"/>: the executor resolves
    /// the latest token inside its first round trip and runs every query of the request against it,
    /// instead of the engine spending a dedicated round trip on it upfront.
    /// </summary>
    public bool ResolveLatestSnapToken { get; init; }
}
