using Authorizee.Core.Data;

namespace Authorizee.Core;

public class PermissionService(IRelationTupleReader tupleReader)
{
    public async Task<bool> Check(CheckRequest req)
    {
        return await Check(new RelationFilter
        {
            Relation = req.Relation,
            EntityId = req.EntityId,
            EntityType = req.EntityType,
            SubjectId = req.UserId
        });

    }

    private async Task<bool> Check(RelationFilter relationFilter)
    {
        var relations = await tupleReader.GetRelations(relationFilter with { SubjectId = null});

        var checkFunctions = new List<Func<Task<bool>>>(capacity: relations.Count);
        
        foreach (var relation in relations)
        {
            if (relation.SubjectId == relationFilter.SubjectId)
            {
                return true;
            }

            if (!relation.IsDirectSubject())
            {
                checkFunctions.Add(() => Check(new RelationFilter
                {
                    Relation = relation.SubjectRelation,
                    EntityType = relation.SubjectType,
                    EntityId = relation.SubjectId,
                    SubjectId = relationFilter.SubjectId
                }));
            }
            
        }

        return await CheckUnion(checkFunctions);
    }

    private async Task<bool> CheckUnion(List<Func<Task<bool>>> functions)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        try
        {
            var results = await Task.WhenAll(functions
                .Select(f =>
                {
                    return Task.Run(f, cancellationToken).ContinueWith(t =>
                    {
                        if (t.Result)
                            cancellationTokenSource.Cancel();

                        return t.Result;
                    }, TaskContinuationOptions.OnlyOnRanToCompletion);
                }));

            if (results.Any(b => b))
            {
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            return true;
        }

        return false;
    }
}