namespace Authorizee.Core.Data;

public interface IRelationTupleReader
{
    Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter);

    Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entitiesIds, string? subjectRelation = null);
    Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter, IEnumerable<SubjectFilter> subjectsFilter);
}