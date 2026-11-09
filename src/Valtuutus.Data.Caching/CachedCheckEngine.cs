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
    private readonly ValtuutusCachingOptions _options;

    public CachedCheckEngine(
        IFusionCache cache,
        [FromKeyedServices(Consts.InnerCheckEngineKey)] ICheckEngine engine)
        : this(cache, engine, new ValtuutusCachingOptions())
    {
    }

    public CachedCheckEngine(
        IFusionCache cache,
        [FromKeyedServices(Consts.InnerCheckEngineKey)] ICheckEngine engine,
        ValtuutusCachingOptions options)
    {
        _cache = cache;
        _engine = engine;
        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask<CheckResult> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        using var span = ValtuutusTelemetry.StartCache("check", req.SnapToken);
        var hit = true;

        try
        {
            if (!ContextHash.TryGetKey(req.Context, out var contextKey))
            {
                // A context value the cache cannot key exactly: answer uncached.
                span.SetCacheHit(false);
                var uncached = await _engine.Check(req, cancellationToken);
                span.SetAllowed(uncached.Allowed);
                return uncached;
            }

            if (req.SnapToken is { } pinned && !await LatestSnapToken.Covers(_cache, pinned, cancellationToken))
            {
                // A token newer than the latest settled one this cache knows may still gain the rows
                // of a write that took its id earlier: the answer at it is not final yet.
                span.SetCacheHit(false);
                var unsettled = await _engine.Check(req, cancellationToken);
                span.SetAllowed(unsettled.Allowed);
                return unsettled;
            }

            if ((req.SnapToken ?? await LatestSnapToken.TryGet(_cache, cancellationToken)) is { } snapToken)
            {
                var resolvedReq = req with { SnapToken = snapToken };
                var cached = await _cache.GetOrSetAsync(GetCheckCacheKey(resolvedReq, snapToken, contextKey),
                    ct =>
                    {
                        hit = false;
                        return _engine.Check(resolvedReq, ct).AsTask();
                    }, _options.ResultDuration(snapToken), cancellationToken);
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
                await _cache.SetAsync(GetCheckCacheKey(req, readAt, contextKey), result, _options.ResultDuration(readAt), token: cancellationToken);
                await LatestSnapToken.Offer(_cache, readAt, _options.LatestSnapTokenDuration, cancellationToken);
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
            if (!ContextHash.TryGetKey(req.Context, out var contextKey))
            {
                // A context value the cache cannot key exactly: answer uncached.
                span.SetCacheHit(false);
                var uncached = await _engine.SubjectPermission(req, cancellationToken);
                return uncached;
            }

            if (req.SnapToken is { } pinned && !await LatestSnapToken.Covers(_cache, pinned, cancellationToken))
            {
                // A token newer than the latest settled one this cache knows may still gain the rows
                // of a write that took its id earlier: the answer at it is not final yet.
                span.SetCacheHit(false);
                var unsettled = await _engine.SubjectPermission(req, cancellationToken);
                return unsettled;
            }

            if ((req.SnapToken ?? await LatestSnapToken.TryGet(_cache, cancellationToken)) is { } snapToken)
            {
                var resolvedReq = req with { SnapToken = snapToken };
                var cached = await _cache.GetOrSetAsync(GetSubjectPermissionCacheKey(resolvedReq, snapToken, contextKey),
                    ct =>
                    {
                        hit = false;
                        return _engine.SubjectPermission(resolvedReq, ct).AsTask();
                    }, _options.ResultDuration(snapToken), cancellationToken);
                span.SetCacheHit(hit);
                return cached;
            }

            span.SetCacheHit(false);
            var result = await _engine.SubjectPermission(req, cancellationToken);
            if (result.SnapToken is { } readAt)
            {
                await _cache.SetAsync(GetSubjectPermissionCacheKey(req, readAt, contextKey), result, _options.ResultDuration(readAt), token: cancellationToken);
                await LatestSnapToken.Offer(_cache, readAt, _options.LatestSnapTokenDuration, cancellationToken);
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

    private static string GetCheckCacheKey(CheckRequest req, SnapToken snapToken, ContextKey contextKey)
    {
        return $"check:{req.EntityType}:{req.EntityId}:{req.Permission}:{req.SubjectType}:{req.SubjectId}:{req.SubjectRelation}:{req.Depth}:{snapToken}{contextKey}";
    }

    private static string GetSubjectPermissionCacheKey(SubjectPermissionRequest req, SnapToken snapToken, ContextKey contextKey)
    {
        return $"subject-permission:{req.EntityType}:{req.EntityId}:{req.SubjectType}:{req.SubjectId}:{req.Depth}:{snapToken.Value}{contextKey}";
    }
}
