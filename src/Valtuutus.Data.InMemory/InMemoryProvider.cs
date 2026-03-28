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

    public Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        Ulid? id;
        lock (_txLock) { id = _latestTransaction; }
        SnapToken? token = id is null ? null : new SnapToken(id.Value.ToString());
        return ExecuteWithRateLimit(_ => Task.FromResult(token), cancellationToken);
    }

    public Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(_relations.GetRelations(tupleFilter)), cancellationToken);
    }

    public Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(_relations.HasDirectRelation(tupleFilter, subjectId)), cancellationToken);
    }

    public Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(_relations.GetIndirectRelations(tupleFilter)), cancellationToken);
    }

    public Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(
            _relations.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entityIds, subjectRelation)),
            cancellationToken);
    }

    public Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
        IList<string> subjectsIds, string subjectType, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(
            _relations.GetRelationsWithSubjectIds(entityFilter, subjectsIds, subjectType)),
            cancellationToken);
    }

    public Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(_attributes.GetAttribute(filter)), cancellationToken);
    }

    public Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(_attributes.GetAttributes(filter)), cancellationToken);
    }

    public Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(
        EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(_attributes.GetByNames(filter)), cancellationToken);
    }

    public Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter,
        IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(
            _attributes.GetAttributesWithEntityIds(filter, entitiesIds)), cancellationToken);
    }

    public Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(
        EntityAttributesFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var _ = DefaultActivitySource.Instance.StartActivity();
        return ExecuteWithRateLimit(_ => Task.FromResult(
            _attributes.GetByNamesWithEntityIds(filter, entitiesIds)), cancellationToken);
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
