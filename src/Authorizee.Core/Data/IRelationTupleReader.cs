namespace Authorizee.Core.Data;

public interface IRelationTupleReader
{
    Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken ct);

    Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entitiesIds, string? subjectRelation, CancellationToken ct);
    Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter, IEnumerable<SubjectFilter> subjectsFilter, CancellationToken ct);
}