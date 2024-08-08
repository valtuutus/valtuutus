namespace Valtuutus.Core.Data;

public interface IDataReaderProvider
{
    Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken);
    Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken);
    Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken);
    Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,  IList<string> subjectsIds, string subjectType, CancellationToken cancellationToken);
    
    Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken);
    Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken);
    Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken);
}