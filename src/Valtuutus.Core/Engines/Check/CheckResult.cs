using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.Check;

/// <summary>The answer to a <see cref="CheckRequest"/>.</summary>
public readonly record struct CheckResult
{
    /// <summary>True if the subject has the permission on the entity.</summary>
    public required bool Allowed { get; init; }

    /// <summary>
    /// The snap token the answer was read at. Pass it as a later request's
    /// <see cref="CheckRequest.SnapToken"/> to read the same snapshot. Null when the request carried
    /// no token and the answer did not depend on any stored data.
    /// </summary>
    public SnapToken? SnapToken { get; init; }
}
