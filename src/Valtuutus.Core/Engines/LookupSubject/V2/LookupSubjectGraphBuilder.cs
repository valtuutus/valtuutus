namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// Turns a lookup request into its graph of queries and set operations by binding the request's
/// ids, depth and context to the compiled plans. The engine and its query runner own execution
/// and the snap token. A dependent hop builds its continuation once the ids it starts from are known.
/// </summary>
internal sealed class LookupSubjectGraphBuilder(LookupSubjectPlanCache plans, LookupSubjectRequest request)
{
    public GraphNode Build(string entityType, string name, IReadOnlyCollection<string> entityIds, int depth)
    {
        // An empty id set must never reach the data layer, where an entity-id filter without ids
        // filters nothing and would return every subject.
        if (depth == 0 || entityIds.Count == 0) return GraphNode.None;

        return Instantiate(plans.GetOrCompile(entityType, name, request.SubjectType), entityIds, depth - 1);
    }

    private GraphNode Instantiate(LookupPlan plan, IReadOnlyCollection<string> entityIds, int depth) => plan switch
    {
        _ when plan == LookupPlan.Empty => GraphNode.None,
        SubjectsPlan { IsHop: true } when depth == 0 => GraphNode.None,
        ExpressionPlan expression when !expression.Expression.IsContextValid(request.Context) => GraphNode.None,
        QueryPlan query => new QueryNode(query.Template.For(entityIds), query, request),
        ExpandPlan expand => depth == 0
            ? GraphNode.None
            : new ExpandNode(expand.Template.For(entityIds), expand, depth, this),
        RefPlan reference => Build(reference.EntityType, reference.Name, entityIds, depth),
        SetPlan set => Set(set, entityIds, depth),
        NegatePlan negate => new NegateNode(Instantiate(negate.Child, entityIds, depth), request.SubjectType),
        _ => throw new InvalidOperationException(),
    };

    private SetNode Set(SetPlan set, IReadOnlyCollection<string> entityIds, int depth)
    {
        var shared = set.Shared?.For(entityIds);
        var nodes = new GraphNode[set.Children.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = set.Children[i] is SlicePlan slice
                ? new QueryNode(shared!, slice, request)
                : Instantiate(set.Children[i], entityIds, depth);
        }
        return new SetNode(nodes, set.Union);
    }
}
