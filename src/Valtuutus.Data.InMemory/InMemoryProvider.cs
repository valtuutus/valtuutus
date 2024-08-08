using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class InMemoryProvider : RateLimiterExecuter, IDataReaderProvider, IDataWriterProvider
{
    private readonly InMemoryController _controller;

    public InMemoryProvider(InMemoryController controller,
        ValtuutusDataOptions options) : base(options)
    {
        _controller = controller;
    }

    public Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        return ExecuteWithRateLimit((ct) => _controller.GetLatestSnapToken(ct), cancellationToken);
    }

    public Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        return ExecuteWithRateLimit((ct) => _controller.GetRelations(tupleFilter, ct), cancellationToken);
    }

    public Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds,
        string? subjectRelation, CancellationToken cancellationToken)
    {
        return ExecuteWithRateLimit((ct) => _controller.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entityIds, subjectRelation, ct), cancellationToken);
    }

    public Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
        IList<string> subjectsIds, string subjectType, CancellationToken cancellationToken)
    {
        return ExecuteWithRateLimit((ct) => _controller.GetRelationsWithSubjectsIds(entityFilter, subjectsIds, subjectType, ct), cancellationToken);
    }
    
    public Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        return ExecuteWithRateLimit((ct) => _controller.GetAttribute(filter, ct), cancellationToken);

    }

    public Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        return ExecuteWithRateLimit((ct) => _controller.GetAttributes(filter, ct), cancellationToken);

    }

    public Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        return ExecuteWithRateLimit((ct) => _controller.GetAttributes(filter, entitiesIds, ct), cancellationToken);
    }


    public Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        var transactId = Ulid.NewUlid().ToString();
        _controller.CreateTransaction(transactId);
        _controller.Write(relations, attributes, ct);
        return Task.FromResult(new SnapToken(transactId));
    }

    public Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        var transactId = Ulid.NewUlid().ToString();
        _controller.CreateTransaction(transactId);
        _controller.Delete(filter, ct);
        return Task.FromResult(new SnapToken(transactId));
    }
}