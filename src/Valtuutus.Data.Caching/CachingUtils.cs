using Valtuutus.Core.Data;
using Valtuutus.Core.Engines;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public static class CachingUtils
{
    public static async Task LoadLatestSnapToken(IDataReaderProvider reader, IFusionCache fusionCache, IWithSnapToken req, CancellationToken cancellationToken)
    {
        if (req.SnapToken is null)
        {
            var latest = await fusionCache.GetOrSetAsync(Consts.LatestSnapTokenKey, reader.GetLatestSnapToken, TimeSpan.FromMinutes(5), cancellationToken);
            req.SnapToken = latest;
        }
    }
}