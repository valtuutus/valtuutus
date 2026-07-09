using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory;

public class InMemoryProvider : RateLimiterExecuter, IDataReaderProvider, IDataWriterProvider
{
    private readonly RelationsStore _relations;
    private readonly AttributesStore _attributes;
    private readonly ValtuutusDataOptions _options;
    private readonly IServiceProvider _provider;

    private readonly object _txLock = new();
    private Ulid? _latestTransaction;

    public InMemoryProvider(RelationsStore relations, AttributesStore attributes,
        ValtuutusDataOptions options, IServiceProvider provider) : base(options)
    {
        _relations = relations;
        _attributes = attributes;
        _options = options;
        _provider = provider;
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        Ulid? id;
        lock (_txLock) { id = _latestTransaction; }
        SnapToken? token = id is null ? null : new SnapToken(id.Value.ToString());
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return token;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.GetRelations(tupleFilter);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.HasDirectRelation(tupleFilter, subjectId);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.HasAnyDirectRelation(entityType, entityIds, relation, subjectId, snapToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<HashSet<string>> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.HasAnyOfDirectRelations(entityType, entityId, relationNames, subjectId, snapToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.GetIndirectRelations(tupleFilter);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entityIds, subjectRelation);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
        string[] subjectsIds, string subjectType, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.GetRelationsWithSubjectIds(entityFilter, subjectsIds, subjectType, scope);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasTupleToUserSetRelation(
        string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.HasTupleToUserSetRelation(entityType, entityId, tupleSetRelation,
                subEntityType, computedRelation, subjectType, subjectId, snapToken);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter, string subEntityType, string subRelation,
        string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.GetRelationsJoined(mainFilter, subEntityType, subRelation, subjectType, subjectId, scope);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _attributes.GetAttribute(filter);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _attributes.GetAttributes(filter);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _attributes.GetByNamesSingleEntity(filter);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(
        EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            HashSet<string>? scopedEntityIds = null;
            if (scope.HasValue)
            {
                var s = scope.Value;
                var scopeFilter = new EntityRelationFilter
                {
                    EntityType = filter.EntityType,
                    Relation = s.Relation,
                    SnapToken = filter.SnapToken
                };
                using var scopedRelations = _relations.GetRelationsWithSubjectIds(scopeFilter, [s.SubjectId], s.SubjectType);
                if (scopedRelations.Count == 0)
                    return new Dictionary<(string, string), AttributeTuple>(0);
                scopedEntityIds = new HashSet<string>(scopedRelations.Count);
                foreach (var r in scopedRelations) scopedEntityIds.Add(r.EntityId);
            }
            return _attributes.GetByNames(filter, scopedEntityIds);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter,
        IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _attributes.GetAttributesWithEntityIds(filter, entitiesIds);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(
        EntityAttributesFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _attributes.GetByNamesWithEntityIds(filter, entitiesIds);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            var ids = _relations.GetAllEntityIds(entityType, snapToken);
            _attributes.GetAllEntityIds(entityType, snapToken, ids);
            var result = new List<string>(ids.Count);
            foreach (var id in ids)
                if (!excludeIds.Contains(id))
                    result.Add(id);
            return result;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            var ids = _relations.GetAllSubjectIds(subjectType, snapToken);
            var result = new List<string>(ids.Count);
            foreach (var id in ids)
                if (!excludeIds.Contains(id))
                    result.Add(id);
            return result;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        var transactId = Ulid.NewUlid();
        await Task.Delay(10, ct);
        lock (_txLock) { if (_latestTransaction is null || transactId.CompareTo(_latestTransaction.Value) > 0) _latestTransaction = transactId; }
        _relations.Write(transactId, relations);
        _attributes.Write(transactId, attributes);
        var snapToken = new SnapToken(transactId.ToString());
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        var transactId = Ulid.NewUlid();
        lock (_txLock) { if (_latestTransaction is null || transactId.CompareTo(_latestTransaction.Value) > 0) _latestTransaction = transactId; }
        _attributes.Delete(transactId, filter.Attributes);
        _relations.Delete(transactId, filter.Relations);
        var snapToken = new SnapToken(transactId.ToString());
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }
}
