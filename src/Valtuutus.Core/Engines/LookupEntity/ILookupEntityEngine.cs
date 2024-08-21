namespace Valtuutus.Core.Engines.LookupEntity;

/// <summary>
/// Provides methods for looking up entities based on specified criteria.
/// </summary>
public interface ILookupEntityEngine
{
    /// <summary>
    /// The LookupEntity method lets you ask "Which resources of type T can entity:X do action Y?"
    /// </summary>
    /// <param name="req">The object containing information about the question being asked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The list of ids of the entities that match the criteria.</returns>
    Task<HashSet<string>> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken);
}