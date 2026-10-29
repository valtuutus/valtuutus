using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Observability;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public sealed class CachedLookupSubjectEngine : ILookupSubjectEngine
{
    private readonly ILookupSubjectEngine _engine;
    private readonly IFusionCache _cache;
    private readonly ValtuutusCachingOptions _options;

    public CachedLookupSubjectEngine(
        [FromKeyedServices(Consts.InnerLookupSubjectEngineKey)] ILookupSubjectEngine engine,
        IFusionCache cache)
        : this(engine, cache, new ValtuutusCachingOptions())
    {
    }

    public CachedLookupSubjectEngine(
        [FromKeyedServices(Consts.InnerLookupSubjectEngineKey)] ILookupSubjectEngine engine,
        IFusionCache cache,
        ValtuutusCachingOptions options)
    {
        _engine = engine;
        _cache = cache;
        _options = options;
    }

    // <inheritdoc />
    public async ValueTask<LookupSubjectResult> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartCache("lookup_subject", req.SnapToken);
        var hit = true;

        try
        {
            if ((req.SnapToken ?? await LatestSnapToken.TryGet(_cache, cancellationToken)) is { } snapToken)
            {
                var resolvedReq = req with { SnapToken = snapToken };
                var cached = await _cache.GetOrSetAsync(GetLookupCacheKey(resolvedReq, snapToken),
                    ct =>
                    {
                        hit = false;
                        return _engine.Lookup(resolvedReq, ct).AsTask();
                    }, _options.ResultDuration(snapToken), cancellationToken);
                span.SetCacheHit(hit);
                return cached;
            }

            // No known token: the engine resolves the latest one, inside its first round trip when the
            // provider can, and reports it.
            span.SetCacheHit(false);
            var result = await _engine.Lookup(req, cancellationToken);
            if (result.SnapToken is { } readAt)
            {
                await _cache.SetAsync(GetLookupCacheKey(req, readAt), result, _options.ResultDuration(readAt), token: cancellationToken);
                await LatestSnapToken.Offer(_cache, readAt, _options.LatestSnapTokenDuration, cancellationToken);
            }
            return result;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    private static string GetLookupCacheKey(LookupSubjectRequest req, SnapToken snapToken)
    {
        return $"lookup-subject:{req.EntityType}:{req.EntityId}:{req.Permission}:{req.SubjectType}:{snapToken.Value}";
    }
}
