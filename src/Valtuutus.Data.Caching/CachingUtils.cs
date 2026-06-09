using Valtuutus.Core.Data;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public static class CachingUtils
{
    public static async ValueTask<SnapToken> ResolveLatest(
        IDataReaderProvider reader, IFusionCache fusionCache,
        SnapToken? current, CancellationToken cancellationToken)
    {
        if (current is { } token)
            return token;
        return await fusionCache.GetOrSetAsync(
            Consts.LatestSnapTokenKey,
            reader.GetLatestSnapToken,
            TimeSpan.FromMinutes(5),
            cancellationToken) ?? SnapToken.MinValue;
    }
}
