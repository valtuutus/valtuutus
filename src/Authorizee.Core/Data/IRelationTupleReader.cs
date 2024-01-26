namespace Authorizee.Core.Data;

public interface IRelationTupleReader
{
    Task<List<RelationTuple>> GetRelations(RelationFilter filter);
}