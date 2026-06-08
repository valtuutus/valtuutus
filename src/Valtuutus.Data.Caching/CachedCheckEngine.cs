using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Observability;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public sealed class CachedCheckEngine : ICheckEngine
{
    private readonly IDataReaderProvider _reader;
    private readonly IFusionCache _cache;
    private readonly CheckEngine _engine;

    public CachedCheckEngine(IDataReaderProvider reader, IFusionCache cache, CheckEngine engine)
    {
        _reader = reader;
        _cache = cache;
        _engine = engine;
    }

    /// <inheritdoc />
    public async Task<bool> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity("CachedCheck");

        var snapToken = await CachingUtils.ResolveLatest(_reader, _cache, req.SnapToken, cancellationToken);
        var resolvedReq = req with { SnapToken = snapToken };
        return await _cache.GetOrSetAsync(GetCheckCacheKey(resolvedReq), ct => _engine.Check(resolvedReq, ct), TimeSpan.FromMinutes(5), cancellationToken);
    }


    /// <inheritdoc />
    public async Task<Dictionary<string, bool>> SubjectPermission(SubjectPermissionRequest req, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity("CachedSubjectPermission");

        var snapToken = await CachingUtils.ResolveLatest(_reader, _cache, req.SnapToken, cancellationToken);
        var resolvedReq = req with { SnapToken = snapToken };
        return await _cache.GetOrSetAsync(GetSubjectPermissionCacheKey(resolvedReq), ct => _engine.SubjectPermission(resolvedReq, ct), TimeSpan.FromMinutes(5), cancellationToken);
    }

    /// <inheritdoc />
    public Task<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken)
        => _engine.Explain(req, cancellationToken);

    private static string GetCheckCacheKey(CheckRequest req)
    {
        return $"check:{req.EntityType}:{req.EntityId}:{req.Permission}:{req.SubjectType}:{req.SubjectId}:{req.SubjectRelation}:{req.SnapToken}";
    }

    private static string GetSubjectPermissionCacheKey(SubjectPermissionRequest req)
    {
        return $"subject-permission:{req.EntityType}:{req.EntityId}:{req.SubjectType}:{req.SubjectId}:{req.SnapToken?.Value}";
    }
}
