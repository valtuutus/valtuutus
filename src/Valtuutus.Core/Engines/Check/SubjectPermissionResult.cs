using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.Check;

/// <summary>The answer to a <see cref="SubjectPermissionRequest"/>.</summary>
public readonly record struct SubjectPermissionResult
{
    /// <summary>Every permission of the entity type, and whether the subject has it.</summary>
    public required Dictionary<string, bool> Permissions { get; init; }

    /// <summary>
    /// The snap token the answer was read at. Pass it as a later request's snap token to read the
    /// same snapshot. Null when the request carried no token and the answer did not depend on any
    /// stored data.
    /// </summary>
    public SnapToken? SnapToken { get; init; }
}
