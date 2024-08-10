namespace Valtuutus.Core.Engines.LookupEntity;

public interface ILookupEntityEngine
{
    /// <summary>
    /// The LookupEntity method lets you ask "Which resources of type T can entity:X do action Y?"
    /// </summary>
    /// <param name="req">The object containing information about the question being asked</param>
    /// <param name="cancellationToken">Cancellation Token</param>
    /// <returns>The list of ids of the entities</returns>
    Task<HashSet<string>> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken);
}