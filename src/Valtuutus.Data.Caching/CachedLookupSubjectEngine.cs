using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Observability;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching;

public sealed class CachedLookupSubjectEngine : ILookupSubjectEngine
{
    private readonly IDataReaderProvider _reader;
    private readonly IFusionCache _cache;
    private readonly LookupSubjectEngine _engine;
    
    public CachedLookupSubjectEngine(IDataReaderProvider reader, LookupSubjectEngine engine, IFusionCache cache)
    {
        _reader = reader;
        _engine = engine;
        _cache = cache;
    }
    
    // <inheritdoc />
    public async Task<HashSet<string>> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity("CachedLookup");

        await CachingUtils.LoadLatestSnapToken(_reader, _cache, req, cancellationToken);
        return await _cache.GetOrSetAsync(GetLookupCacheKey(req), ct => _engine.Lookup(req, ct), TimeSpan.FromMinutes(5), cancellationToken);
    }
    
    private static string GetLookupCacheKey(LookupSubjectRequest req)
    {
        return $"lookup-subject:{req.EntityType}:{req.EntityId}:{req.Permission}:{req.SubjectType}:{req.SnapToken?.Value}";
    }
}