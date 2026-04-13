namespace Valtuutus.Core.Engines.LookupEntity;

/// <summary>
/// A single page of LookupEntity results, ordered lexicographically by entity ID.
/// Pass <see cref="ContinuationToken"/> back on the next request to get the following page.
/// </summary>
public readonly record struct LookupEntityPage(
    IReadOnlyList<string> EntityIds,
    string? ContinuationToken
);
