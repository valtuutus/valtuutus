using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;

namespace Valtuutus.Data.InMemory;

internal sealed class InMemoryProvider : RateLimiterExecuter, IDataReaderProvider, IDataWriterProvider
{
    private readonly InMemoryController _controller;
    private readonly ValtuutusDataOptions _options;
    private readonly IServiceProvider _provider;

    public InMemoryProvider(InMemoryController controller,
        ValtuutusDataOptions options, IServiceProvider provider) : base(options)
    {
        _controller = controller;
        _options = options;
        _provider = provider;
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit((ct) => _controller.GetLatestSnapToken(ct), cancellationToken);
    }

    public async Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        return await ExecuteWithRateLimit((ct) => _controller.GetRelations(tupleFilter, ct), cancellationToken);
    }

    public async Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds,
        string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit((ct) => _controller.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entityIds, subjectRelation, ct), cancellationToken);
    }

    public async Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
        IList<string> subjectsIds, string subjectType, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit((ct) => _controller.GetRelationsWithSubjectsIds(entityFilter, subjectsIds, subjectType, ct), cancellationToken);
    }
    
    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit((ct) => _controller.GetAttribute(filter, ct), cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        return await ExecuteWithRateLimit((ct) => _controller.GetAttributes(filter, ct), cancellationToken);

    }

    public async Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        return await ExecuteWithRateLimit((ct) => _controller.GetAttributes(filter, entitiesIds, ct), cancellationToken);
    }


    public async Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();

        var transactId = Ulid.NewUlid().ToString();
        _controller.CreateTransaction(transactId);
        _controller.Write(transactId, relations, attributes, ct);
        var snapToken = new SnapToken(transactId);    
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
    }

    public async Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        var transactId = Ulid.NewUlid().ToString();
        _controller.CreateTransaction(transactId);
        _controller.Delete(filter, ct);
        var snapToken = new SnapToken(transactId);    
        await (_options.OnDataWritten?.Invoke(_provider, snapToken) ?? Task.CompletedTask);
        return snapToken;
        
    }
}