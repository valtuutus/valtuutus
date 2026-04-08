using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class InMemoryProvider : RateLimiterExecuter, IDataReaderProvider, IDataWriterProvider
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
        string[] subjectsIds, string subjectType, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.GetRelationsWithSubjectIds(entityFilter, subjectsIds, subjectType);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter, string subEntityType, string subRelation,
        string subjectType, string subjectId, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _relations.GetRelationsJoined(mainFilter, subEntityType, subRelation, subjectType, subjectId);
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

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(
        EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            return _attributes.GetByNames(filter);
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

    public async Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        var transactId = Ulid.NewUlid();
        await Task.Delay(10, ct);
        lock (_txLock) { _latestTransaction = transactId; }
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
        lock (_txLock) { _latestTransaction = transactId; }
        _attributes.Delete(transactId, filter.Attributes);
        _relations.Delete(transactId, filter.Relations);
        var snapToken = new SnapToken(transactId.ToString());
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }
}
