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
    private readonly ValtuutusCachingOptions _options;

    public CachedLookupEntityEngine(
        [FromKeyedServices(Consts.InnerLookupEntityEngineKey)] ILookupEntityEngine engine,
        IFusionCache cache)
        : this(engine, cache, new ValtuutusCachingOptions())
    {
    }

    public CachedLookupEntityEngine(
        [FromKeyedServices(Consts.InnerLookupEntityEngineKey)] ILookupEntityEngine engine,
        IFusionCache cache,
        ValtuutusCachingOptions options)
    {
        _engine = engine;
        _cache = cache;
        _options = options;
    }

    public async ValueTask<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartCache("lookup_entity", req.SnapToken);
        var hit = true;

        try
        {
            if (!ContextHash.TryGetKey(req.Context, out var contextKey))
            {
                // A context value the cache cannot key exactly: answer uncached.
                span.SetCacheHit(false);
                var uncached = await _engine.LookupEntity(req, cancellationToken);
                return uncached;
            }

            if (req.SnapToken is { } pinned && !await LatestSnapToken.Covers(_cache, pinned, cancellationToken))
            {
                // A token newer than the latest settled one this cache knows may still gain the rows
                // of a write that took its id earlier: the answer at it is not final yet.
                span.SetCacheHit(false);
                var unsettled = await _engine.LookupEntity(req, cancellationToken);
                return unsettled;
            }

            if ((req.SnapToken ?? await LatestSnapToken.TryGet(_cache, cancellationToken)) is { } snapToken)
            {
                var resolvedReq = req with { SnapToken = snapToken };
                var cached = await _cache.GetOrSetAsync(GetLookupCacheKey(resolvedReq, snapToken, contextKey),
                    ct =>
                    {
                        hit = false;
                        return _engine.LookupEntity(resolvedReq, ct).AsTask();
                    }, _options.ResultDuration(snapToken), cancellationToken);
                span.SetCacheHit(hit);
                return cached;
            }

            // No known token: the engine resolves the latest one, inside its first round trip when the
            // provider can, and reports it.
            span.SetCacheHit(false);
            var result = await _engine.LookupEntity(req, cancellationToken);
            if (result.SnapToken is { } readAt)
            {
                await _cache.SetAsync(GetLookupCacheKey(req, readAt, contextKey), result, _options.ResultDuration(readAt), token: cancellationToken);
                await LatestSnapToken.Offer(_cache, readAt, _options.LatestSnapTokenDuration, cancellationToken);
            }
            return result;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    private static string GetLookupCacheKey(LookupEntityRequest req, SnapToken snapToken, ContextKey contextKey)
    {
        var scopePart = req.Scope is { } s
            ? $":{s.Relation}:{s.SubjectType}:{s.SubjectId}"
            : ":";
        return $"lookup-entity:{req.EntityType}:{req.Permission}:{req.SubjectType}:{req.SubjectId}:{req.Depth}:{snapToken.Value}{scopePart}:{req.PageSize}:{req.ContinuationToken}{contextKey}";
    }
}
