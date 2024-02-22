using Authorizee.Core;
using Authorizee.Core.Data;

namespace Authorizee.Tests;

public class InMemoryRelationTupleReader : IRelationTupleReader
{
    private readonly RelationTuple[] _relationTuples;

    public InMemoryRelationTupleReader(RelationTuple[] relationTuples)
    {
        _relationTuples = relationTuples;
    }

    public Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter)
    {
        var result = _relationTuples
            .Where(x => x.EntityType == tupleFilter.EntityType
                        && x.EntityId == tupleFilter.EntityId
                        && x.Relation == tupleFilter.Relation);

        if (!string.IsNullOrEmpty(tupleFilter.SubjectId))
            result = result.Where(x => x.SubjectId == tupleFilter.SubjectId);

        if (!string.IsNullOrEmpty(tupleFilter.SubjectRelation))
            result = result.Where(x => x.SubjectRelation == tupleFilter.SubjectRelation);

        if (!string.IsNullOrEmpty(tupleFilter.SubjectType))
            result = result.Where(x => x.SubjectType == tupleFilter.SubjectType);

        return Task.FromResult(result.ToList());
    }

    public Task<List<RelationTuple>> GetRelations(IEnumerable<EntityRelationFilter> filters,
        SubjectFilter? subjectFilter)
    {
        var result = _relationTuples.AsEnumerable();

        if (!string.IsNullOrEmpty(subjectFilter?.SubjectId))
            result = result.Where(x => x.SubjectId == subjectFilter.SubjectId);

        if (!string.IsNullOrEmpty(subjectFilter?.SubjectType))
            result = result.Where(x => x.SubjectType == subjectFilter.SubjectType);

        return Task.FromResult(result.Where(x =>
                filters.Any(y => x.EntityType == y.EntityType
                                 && x.Relation == y.Relation))
            .ToList()
        );
    }

    public Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter, IEnumerable<SubjectFilter> subjectsFilter)
    {
        var result = _relationTuples.AsEnumerable();
        
        if (!string.IsNullOrEmpty(entityFilter.EntityType))
            result = result.Where(x => x.EntityType == entityFilter.EntityType);

        if (!string.IsNullOrEmpty(entityFilter.Relation))
            result = result.Where(x => x.Relation == entityFilter.Relation);
        
        return Task.FromResult(result.Where(x =>
                subjectsFilter.Any(y => x.SubjectType == y.SubjectType
                                        && x.SubjectId == y.SubjectId))
            .ToList()
        );
    }
}