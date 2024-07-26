namespace Valtuutus.Core.Data;

public interface IDataReaderProvider
{
    Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken ct);
    Task<List<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entityIds, string? subjectRelation, CancellationToken ct);
    Task<List<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,  IList<string> subjectsIds, string subjectType, CancellationToken ct);
    
    Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken ct);
    Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken ct);
    Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken ct);
}