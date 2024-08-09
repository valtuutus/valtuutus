using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public sealed class CachedLookupEntityEngine : ILookupEntityEngine
{
    private readonly IDataReaderProvider _reader;
    private readonly IFusionCache _cache;
    private readonly LookupEntityEngine _engine;

    public CachedLookupEntityEngine(IDataReaderProvider reader, LookupEntityEngine engine, IFusionCache cache)
    {
        _reader = reader;
        _engine = engine;
        _cache = cache;
    }

    // <inheritdoc />
    public async Task<HashSet<string>> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity("CachedLookupEntity");

        if (req.SnapToken is null)
        {
            var latest = await _cache.GetOrSetAsync(Consts.LatestSnapTokenKey, ct => _reader.GetLatestSnapToken(ct), TimeSpan.FromMinutes(5), cancellationToken);
            req.SnapToken = latest;
        }
        return await _cache.GetOrSetAsync(GetLookupCacheKey(req), ct => _engine.LookupEntity(req, ct), TimeSpan.FromMinutes(5), cancellationToken);
    }
    
    private static string GetLookupCacheKey(LookupEntityRequest req)
    {
        return $"lookup-entity:{req.EntityType}:{req.Permission}:{req.SubjectType}:{req.SubjectId}:{req.SnapToken?.Value}";
    }
}