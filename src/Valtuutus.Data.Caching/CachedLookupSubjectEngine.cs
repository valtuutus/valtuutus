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

    public CachedLookupSubjectEngine(
        [FromKeyedServices(Consts.InnerLookupSubjectEngineKey)] ILookupSubjectEngine engine,
        IFusionCache cache)
    {
        _engine = engine;
        _cache = cache;
    }

    // <inheritdoc />
    public async ValueTask<LookupSubjectResult> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTracing.StartCache("lookup_subject");
        var hit = true;

        if ((req.SnapToken ?? await LatestSnapToken.TryGet(_cache, cancellationToken)) is { } snapToken)
        {
            var resolvedReq = req with { SnapToken = snapToken };
            var cached = await _cache.GetOrSetAsync(GetLookupCacheKey(resolvedReq, snapToken),
                ct =>
                {
                    hit = false;
                    return _engine.Lookup(resolvedReq, ct).AsTask();
                }, LatestSnapToken.Duration, cancellationToken);
            span.SetCacheHit(hit);
            return cached;
        }

        // No known token: the engine resolves the latest one, inside its first round trip when the
        // provider can, and reports it.
        span.SetCacheHit(false);
        var result = await _engine.Lookup(req, cancellationToken);
        if (result.SnapToken is { } readAt)
        {
            await _cache.SetAsync(GetLookupCacheKey(req, readAt), result, LatestSnapToken.Duration, token: cancellationToken);
            await LatestSnapToken.Offer(_cache, readAt, cancellationToken);
        }
        return result;
    }

    private static string GetLookupCacheKey(LookupSubjectRequest req, SnapToken snapToken)
    {
        return $"lookup-subject:{req.EntityType}:{req.EntityId}:{req.Permission}:{req.SubjectType}:{snapToken.Value}";
    }
}
