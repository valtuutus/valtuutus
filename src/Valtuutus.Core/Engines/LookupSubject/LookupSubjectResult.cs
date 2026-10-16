using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.LookupSubject;

/// <summary>The answer to a <see cref="LookupSubjectRequest"/>.</summary>
public readonly record struct LookupSubjectResult
{
    /// <summary>Ids of the subjects of the requested type that have the permission on the entity.</summary>
    public required HashSet<string> SubjectIds { get; init; }

    /// <summary>
    /// The snap token the answer was read at. Pass it as a later request's snap token to read the
    /// same snapshot. Null when the request carried no token and the answer did not depend on any
    /// stored data.
    /// </summary>
    public SnapToken? SnapToken { get; init; }
}
