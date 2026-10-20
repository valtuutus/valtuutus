using Valtuutus.Core.Data;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

/// <summary>
/// The latest snap token this cache knows. A request without a token reads at it, so a cached
/// result answers the request without a round trip.
/// </summary>
internal static class LatestSnapToken
{
    internal static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

    public static async ValueTask<SnapToken?> TryGet(IFusionCache cache, CancellationToken cancellationToken)
    {
        var latest = await cache.TryGetAsync<SnapToken>(Consts.LatestSnapTokenKey, token: cancellationToken);
        return latest.HasValue ? latest.Value : null;
    }

    /// <summary>
    /// Records a token as the latest (one an engine resolved, or one a committed write announced),
    /// unless the cache already knows a newer one: a read that resolved its token before a write
    /// landed, or a write that finished after a newer one, must not hide that newer write from later
    /// requests.
    /// </summary>
    public static async ValueTask Offer(IFusionCache cache, SnapToken snapToken, CancellationToken cancellationToken,
        Action<FusionCacheEntryOptions>? setupEntry = null)
    {
        var current = await TryGet(cache, cancellationToken);
        if (current is { } known && string.CompareOrdinal(known.Value, snapToken.Value) >= 0)
            return;
        await cache.SetAsync(Consts.LatestSnapTokenKey, snapToken, options =>
        {
            options.SetDuration(Duration);
            setupEntry?.Invoke(options);
        }, cancellationToken);
    }
}
