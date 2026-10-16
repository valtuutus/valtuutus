namespace Valtuutus.Core.Engines.LookupEntity;

/// <summary>
/// Provides methods for looking up entities based on specified criteria.
/// </summary>
public interface ILookupEntityEngine
{
    /// <summary>
    /// The LookupEntity method lets you ask "Which resources of type T can entity:X do action Y?"
    /// Optionally scoped to a parent entity via <see cref="LookupEntityRequest.Scope"/>.
    /// Optionally paginated via <see cref="LookupEntityRequest.PageSize"/> and <see cref="LookupEntityRequest.ContinuationToken"/>.
    /// </summary>
    /// <param name="req">The object containing information about the question being asked.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A page of entity IDs, an optional continuation token for the next page, and the snap token the page was read at.</returns>
    ValueTask<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken);
}