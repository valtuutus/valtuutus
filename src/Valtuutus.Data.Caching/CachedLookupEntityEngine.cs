using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public sealed class CachedLookupEntityEngine : ILookupEntityEngine
{
    private readonly ILookupEntityEngine _engine;
    private readonly IFusionCache _cache;

    public CachedLookupEntityEngine(
        [FromKeyedServices(Consts.InnerLookupEntityEngineKey)] ILookupEntityEngine engine,
        IFusionCache cache)
    {
        _engine = engine;
        _cache = cache;
    }

    public async ValueTask<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTracing.StartCache("lookup_entity");
        var hit = true;

        if ((req.SnapToken ?? await LatestSnapToken.TryGet(_cache, cancellationToken)) is { } snapToken)
        {
            var resolvedReq = req with { SnapToken = snapToken };
            var cached = await _cache.GetOrSetAsync(GetLookupCacheKey(resolvedReq, snapToken),
                ct =>
                {
                    hit = false;
                    return _engine.LookupEntity(resolvedReq, ct).AsTask();
                }, LatestSnapToken.Duration, cancellationToken);
            span.SetCacheHit(hit);
            return cached;
        }

        // No known token: the engine resolves the latest one, inside its first round trip when the
        // provider can, and reports it.
        span.SetCacheHit(false);
        var result = await _engine.LookupEntity(req, cancellationToken);
        if (result.SnapToken is { } readAt)
        {
            await _cache.SetAsync(GetLookupCacheKey(req, readAt), result, LatestSnapToken.Duration, token: cancellationToken);
            await LatestSnapToken.Offer(_cache, readAt, cancellationToken);
        }
        return result;
    }

    private static string GetLookupCacheKey(LookupEntityRequest req, SnapToken snapToken)
    {
        var scopePart = req.Scope is { } s
            ? $":{s.Relation}:{s.SubjectType}:{s.SubjectId}"
            : ":";
        return $"lookup-entity:{req.EntityType}:{req.Permission}:{req.SubjectType}:{req.SubjectId}:{snapToken.Value}{scopePart}:{req.PageSize}:{req.ContinuationToken}";
    }
}
