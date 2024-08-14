namespace Valtuutus.Core.Engines.LookupSubject;

/// <summary>
/// Provides methods for looking up subjects based on specified criteria.
/// </summary>
public interface ILookupSubjectEngine
{
    /// <summary>
    /// The LookupSubject method lets you ask "Which subjects of type T can do action Y on entity:X?"
    /// </summary>
    /// <param name="req">The object containing information for which subjects to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The list of ids of subjects of the provided type that have the permission on the specified entity.</returns>
    Task<HashSet<string>> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken);
}