using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public sealed class CachedLookupEntityEngine : ILookupEntityEngine
{
    private readonly IDataReaderProvider _reader;
    private readonly IFusionCache _cache;
    private readonly ILookupEntityEngine _engine;

    public CachedLookupEntityEngine(
        IDataReaderProvider reader,
        [FromKeyedServices(Consts.InnerLookupEntityEngineKey)] ILookupEntityEngine engine,
        IFusionCache cache)
    {
        _reader = reader;
        _engine = engine;
        _cache = cache;
    }

    public async Task<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity("CachedLookupEntity");

        var snapToken = await CachingUtils.ResolveLatest(_reader, _cache, req.SnapToken, cancellationToken);
        var resolvedReq = req with { SnapToken = snapToken };
        return await _cache.GetOrSetAsync(GetLookupCacheKey(resolvedReq), ct => _engine.LookupEntity(resolvedReq, ct), TimeSpan.FromMinutes(5), cancellationToken);
    }

    private static string GetLookupCacheKey(LookupEntityRequest req)
    {
        var scopePart = req.Scope is { } s
            ? $":{s.Relation}:{s.SubjectType}:{s.SubjectId}"
            : ":";
        return $"lookup-entity:{req.EntityType}:{req.Permission}:{req.SubjectType}:{req.SubjectId}:{req.SnapToken?.Value}{scopePart}:{req.PageSize}:{req.ContinuationToken}";
    }
}
