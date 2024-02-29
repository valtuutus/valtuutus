namespace Authorizee.Core.Data;

public interface IDataReaderProvider
{
    Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken ct);
    Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entitiesIds, string? subjectRelation, CancellationToken ct);
    Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter,  IList<string> subjectsIds, string subjectType, CancellationToken ct);
    
    Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken ct);
    Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken ct);
    Task<List<AttributeTuple>> GetAttributes(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken ct);
}