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

    public Task<List<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken ct)
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

    public Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityRelationFilter, string subjectType,
        IEnumerable<string> entitiesIds,
        string? subjectRelation, CancellationToken ct)
    {
        return Task.FromResult(_relationTuples
            .Where(x => x.EntityType == entityRelationFilter.EntityType
                        && x.Relation == entityRelationFilter.Relation
                        && x.SubjectType == subjectType && entitiesIds.Contains(x.EntityId)
            )
            .ToList());
    }

    public Task<List<RelationTuple>> GetRelations(EntityRelationFilter entityFilter,
        IList<string> subjectsIds, string subjectType, CancellationToken ct)
    {
        var result = _relationTuples.AsEnumerable();

        if (!string.IsNullOrEmpty(entityFilter.EntityType))
            result = result.Where(x => x.EntityType == entityFilter.EntityType);

        if (!string.IsNullOrEmpty(entityFilter.Relation))
            result = result.Where(x => x.Relation == entityFilter.Relation);


        result = result.Where(x => x.SubjectType == subjectType);
        return Task.FromResult(result.Where(x =>
                subjectsIds.Any(y => x.SubjectId == y))
            .ToList()
        );
    }
}