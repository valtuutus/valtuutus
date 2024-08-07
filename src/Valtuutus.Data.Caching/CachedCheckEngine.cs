using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
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
        req = req.SnapToken is not null ? req with { SnapToken = await _reader.GetLatestSnapToken(cancellationToken) } : req;
        return await _cache.GetOrSetAsync(GetCheckCacheKey(req), ct => _engine.Check(req, ct), TimeSpan.FromMinutes(5), cancellationToken);
    }

    
    /// <inheritdoc />
    public async Task<Dictionary<string, bool>> SubjectPermission(SubjectPermissionRequest req, CancellationToken cancellationToken)
    {
        req = req.SnapToken is not null ? req with { SnapToken = await _reader.GetLatestSnapToken(cancellationToken) } : req;
        return await _cache.GetOrSetAsync(GetSubjectPermissionCacheKey(req), ct => _engine.SubjectPermission(req, ct), TimeSpan.FromMinutes(5), cancellationToken);
    }
    
    private static string GetCheckCacheKey(CheckRequest req)
    {
        return $"check:{req.EntityType}:{req.EntityId}:{req.Permission}:{req.SubjectType}:{req.SubjectId}:{req.SubjectRelation}:{req.SnapToken?.Value}";
    }
    
    private static string GetSubjectPermissionCacheKey(SubjectPermissionRequest req)
    {
        return $"subject-permission:{req.EntityType}:{req.EntityId}:{req.SubjectType}:{req.SubjectId}:{req.SnapToken?.Value}";
    }
}