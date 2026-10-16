using Valtuutus.Core.Data;

namespace Valtuutus.Core.Engines.LookupEntity;

/// <summary>
/// A single page of LookupEntity results, ordered lexicographically by entity ID.
/// Pass <see cref="ContinuationToken"/> back on the next request to get the following page.
/// </summary>
public readonly record struct LookupEntityPage(
    IReadOnlyList<string> EntityIds,
    string? ContinuationToken
)
{
    /// <summary>
    /// The snap token the page was read at. Pass it as the next page's
    /// <see cref="LookupEntityRequest.SnapToken"/> to read every page from the same snapshot; leave
    /// the next request's token unset to read each page at the latest one. Null when the request
    /// carried no token and the answer did not depend on any stored data.
    /// </summary>
    public SnapToken? SnapToken { get; init; }
}
