using System.Runtime.CompilerServices;
using Valtuutus.Core.Data;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

/// <summary>
/// The latest snap token this cache knows. A request without a token reads at it, so a cached
/// result answers the request without a round trip.
/// </summary>
internal static class LatestSnapToken
{
    // The newest token this process has seen per cache. The cache entry alone cannot keep the latest
    // token from moving backwards: its get and set are separate operations, so concurrent offers (from
    // this process or another node sharing the distributed cache) can land an older token last.
    private static readonly ConditionalWeakTable<IFusionCache, NewestSeen> Seen = new();

    public static async ValueTask<SnapToken?> TryGet(IFusionCache cache, CancellationToken cancellationToken)
    {
        var latest = await cache.TryGetAsync<SnapToken>(Consts.LatestSnapTokenKey, token: cancellationToken);
        return latest.HasValue ? new SnapToken(Seen.GetOrCreateValue(cache).Advance(latest.Value.Value)) : null;
    }

    /// <summary>
    /// Records a token as the latest (one an engine resolved, or one a committed write announced),
    /// unless a newer one is already known: a read that resolved its token before a write landed, or
    /// a write that finished after a newer one, must not hide that newer write from later requests.
    /// </summary>
    public static async ValueTask Offer(IFusionCache cache, SnapToken snapToken, TimeSpan duration,
        CancellationToken cancellationToken, Action<FusionCacheEntryOptions>? setupEntry = null)
    {
        var seen = Seen.GetOrCreateValue(cache);
        var cached = await cache.TryGetAsync<SnapToken>(Consts.LatestSnapTokenKey, token: cancellationToken);
        if (cached.HasValue) seen.Advance(cached.Value.Value);
        var newest = seen.Advance(snapToken.Value);
        if (cached.HasValue && string.CompareOrdinal(cached.Value.Value, newest) >= 0)
            return;

        // A racing offer may still land an older token after this one; reads in this process take the
        // newest token seen anyway, and the next offer brings the entry forward.
        await cache.SetAsync(Consts.LatestSnapTokenKey, new SnapToken(newest), options =>
        {
            options.SetDuration(duration);
            setupEntry?.Invoke(options);
        }, cancellationToken);
    }

    private sealed class NewestSeen
    {
        private string? _value;

        /// <summary>Moves the newest token forward to <paramref name="candidate"/> if it is newer,
        /// and returns the newest token.</summary>
        public string Advance(string candidate)
        {
            var current = Volatile.Read(ref _value);
            while (current is null || string.CompareOrdinal(current, candidate) < 0)
            {
                var witnessed = Interlocked.CompareExchange(ref _value, candidate, current);
                if (ReferenceEquals(witnessed, current)) return candidate;
                current = witnessed;
            }

            return current;
        }
    }
}
