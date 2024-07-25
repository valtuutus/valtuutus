using IdGen;
using Sqids;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class InMemoryProvider : RateLimiterExecuter, IDataReaderProvider, IDataWriterProvider
{
    private readonly InMemoryController _controller;
    private readonly IIdGenerator<long> _idGenerator;
    private readonly SqidsEncoder<long> _encoder;

    public InMemoryProvider(InMemoryController controller, IIdGenerator<long> idGenerator, SqidsEncoder<long> encoder,
        ValtuutusDataOptions options) : base(options)
    {
        _controller = controller;
        _idGenerator = idGenerator;
        _encoder = encoder;
    }

    public Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken ct)
    {
        return _controller.GetRelations(tupleFilter, ct);
    }

    public Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds,
        string? subjectRelation, CancellationToken ct)
    {
        return _controller.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entityIds, subjectRelation, ct);
    }

    public Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
        IList<string> subjectsIds, string subjectType, CancellationToken ct)
    {
        return _controller.GetRelationsWithSubjectsIds(entityFilter, subjectsIds, subjectType, ct);
    }
    
    public Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken ct)
    {
        return _controller.GetAttribute(filter, ct);

    }

    public Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken ct)
    {
        return _controller.GetAttributes(filter, ct);

    }

    public Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken ct)
    {
        return _controller.GetAttributes(filter, entitiesIds, ct);
    }


    public Task<SnapToken> Write(IEnumerable<RelationTuple> relations, IEnumerable<AttributeTuple> attributes, CancellationToken ct)
    {
        var transactId = _idGenerator.CreateId();
        _controller.Write(relations, attributes, ct);
        return Task.FromResult(new SnapToken(_encoder.Encode(transactId)));
    }

    public Task<SnapToken> Delete(DeleteFilter filter, CancellationToken ct)
    {
        var transactId = _idGenerator.CreateId();
        _controller.Delete(filter, ct);
        return Task.FromResult(new SnapToken(_encoder.Encode(transactId)));
    }
}