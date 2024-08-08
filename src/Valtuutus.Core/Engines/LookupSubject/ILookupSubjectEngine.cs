namespace Valtuutus.Core.Engines.LookupSubject;

public interface ILookupSubjectEngine
{
    /// <summary>
    /// The LookupSubject lets you ask "Which subjects of type T can do action Y on entity:X?"
    /// </summary>
    /// <param name="req">The object containing information for which </param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The list of ids of subjects of the provided type that has the permission on the specified entity.</returns>
    Task<HashSet<string>> Lookup(LookupSubjectRequest req, CancellationToken ct);
}