using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Observability;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public sealed class CachedCheckEngine : ICheckEngine
{
    private readonly IFusionCache _cache;
    private readonly ICheckEngine _engine;

    public CachedCheckEngine(
        IFusionCache cache,
        [FromKeyedServices(Consts.InnerCheckEngineKey)] ICheckEngine engine)
    {
        _cache = cache;
        _engine = engine;
    }

    /// <inheritdoc />
    public async ValueTask<CheckResult> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartCache("check", req.SnapToken);
        var hit = true;

        try
        {
            if ((req.SnapToken ?? await LatestSnapToken.TryGet(_cache, cancellationToken)) is { } snapToken)
            {
                var resolvedReq = req with { SnapToken = snapToken };
                var cached = await _cache.GetOrSetAsync(GetCheckCacheKey(resolvedReq, snapToken),
                    ct =>
                    {
                        hit = false;
                        return _engine.Check(resolvedReq, ct).AsTask();
                    }, LatestSnapToken.Duration, cancellationToken);
                span.SetCacheHit(hit);
                span.SetAllowed(cached.Allowed);
                return cached;
            }

            // No known token: the engine resolves the latest one, inside its first round trip when the
            // provider can, and reports it.
            span.SetCacheHit(false);
            var result = await _engine.Check(req, cancellationToken);
            if (result.SnapToken is { } readAt)
            {
                await _cache.SetAsync(GetCheckCacheKey(req, readAt), result, LatestSnapToken.Duration, token: cancellationToken);
                await LatestSnapToken.Offer(_cache, readAt, cancellationToken);
            }
            span.SetAllowed(result.Allowed);
            return result;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<SubjectPermissionResult> SubjectPermission(SubjectPermissionRequest req, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartCache("subject_permission", req.SnapToken);
        var hit = true;

        try
        {
            if ((req.SnapToken ?? await LatestSnapToken.TryGet(_cache, cancellationToken)) is { } snapToken)
            {
                var resolvedReq = req with { SnapToken = snapToken };
                var cached = await _cache.GetOrSetAsync(GetSubjectPermissionCacheKey(resolvedReq, snapToken),
                    ct =>
                    {
                        hit = false;
                        return _engine.SubjectPermission(resolvedReq, ct).AsTask();
                    }, LatestSnapToken.Duration, cancellationToken);
                span.SetCacheHit(hit);
                return cached;
            }

            span.SetCacheHit(false);
            var result = await _engine.SubjectPermission(req, cancellationToken);
            if (result.SnapToken is { } readAt)
            {
                await _cache.SetAsync(GetSubjectPermissionCacheKey(req, readAt), result, LatestSnapToken.Duration, token: cancellationToken);
                await LatestSnapToken.Offer(_cache, readAt, cancellationToken);
            }
            return result;
        }
        catch (Exception e) when (span.RecordFailure(e))
        {
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken)
        => _engine.Explain(req, cancellationToken);

    private static string GetCheckCacheKey(CheckRequest req, SnapToken snapToken)
    {
        return $"check:{req.EntityType}:{req.EntityId}:{req.Permission}:{req.SubjectType}:{req.SubjectId}:{req.SubjectRelation}:{snapToken}";
    }

    private static string GetSubjectPermissionCacheKey(SubjectPermissionRequest req, SnapToken snapToken)
    {
        return $"subject-permission:{req.EntityType}:{req.EntityId}:{req.SubjectType}:{req.SubjectId}:{snapToken.Value}";
    }
}
