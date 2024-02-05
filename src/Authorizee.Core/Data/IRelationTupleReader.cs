namespace Authorizee.Core.Data;

public interface IRelationTupleReader
{
    Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter);
    Task<List<RelationTuple>> GetRelations(IEnumerable<EntityRelationFilter> filters, SubjectFilter? subjectFilter);
}