namespace Valtuutus.Core.Engines.LookupEntity.V2;

/// <summary>
/// Turns a LookupEntity request into its graph of queries and set operations by binding the
/// request's subject, depth, context and scope to the compiled plans. The root frame and the
/// permissions it references read within the request's scope; a dependent hop starts a frame
/// without it. Queries that depend on no data are shared per request, so each runs once.
/// </summary>
internal sealed class LookupEntityGraphBuilder(LookupEntityPlanCache plans, LookupEntityRequest request)
{
    private readonly string[] _subject = [request.SubjectId];
    private Dictionary<SharedKey, LookupEntityQuery>? _shared;
    private LookupEntityQuery? _scopeIds;

    public EntityNode Build(string entityType, string name, int depth, bool scoped)
    {
        if (depth == 0) return EntityNode.None;
        return Instantiate(plans.GetOrCompile(entityType, name, request.SubjectType), depth - 1, scoped);
    }

    private EntityNode Instantiate(LookupEntityPlan plan, int depth, bool scoped) => plan switch
    {
        _ when plan == LookupEntityPlan.Empty => EntityNode.None,
        SubjectBoundPlan { IsHop: true } when depth == 0 => EntityNode.None,
        SubjectBoundPlan bound => new QueryNode(Shared(bound.Template, _subject, scoped), bound, request),
        ExpressionPlan expression => expression.Expression.IsContextValid(request.Context)
            ? new QueryNode(Shared(expression.Template, [], scoped), expression, request)
            : EntityNode.None,
        AttributePlan attribute => new AttributeNode(Shared(attribute.Template, [], scoped: false), ScopeIds(scoped)),
        ExpandPlan expand => depth == 0
            ? EntityNode.None
            : new ExpandNode(Build(expand.EntityType, expand.Name, depth, scoped: false), expand.Main, Scope(scoped)),
        RefPlan reference => Build(reference.EntityType, reference.Name, depth, scoped),
        SetPlan set => new SetNode(Children(set.Children, set.Shared, depth, scoped), set.Union),
        ConstrainedIntersectPlan constrained => new ConstrainedIntersectNode(
            Children(constrained.Others, constrained.Shared, depth, scoped), constrained.Checks, request),
        NegateIntersectPlan negate => new NegateIntersectNode(
            Children(negate.Positives, negate.Shared, depth, scoped), Children(negate.Negated, null, depth, scoped)),
        NegatePlan negate => new NegateNode(Instantiate(negate.Child, depth, scoped), negate.EntityType, ScopeIds(scoped)),
        _ => throw new InvalidOperationException(),
    };

    private EntityNode[] Children(LookupEntityPlan[] children, LookupEntityQuery? shared, int depth, bool scoped)
    {
        var bound = shared is null ? null : Shared(shared, _subject, scoped);
        var nodes = new EntityNode[children.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = children[i] is SlicePlan slice
                ? new QueryNode(bound!, slice, request)
                : Instantiate(children[i], depth, scoped);
        }
        return nodes;
    }

    private EntityScope? Scope(bool scoped) => scoped ? request.Scope : null;

    // A query bound to the request's subject, or to no ids, asks the same thing wherever the graph
    // reaches it, so one instance per shape serves every node.
    private LookupEntityQuery Shared(LookupEntityQuery template, string[] ids, bool scoped)
    {
        var key = new SharedKey(template.Kind, template.EntityType, template.Relation, string.Join('\0', template.Names),
            template.SubjectType, template.SubjectRelation, template.SubEntityType, template.SubRelation, scoped);
        _shared ??= new Dictionary<SharedKey, LookupEntityQuery>();
        if (!_shared.TryGetValue(key, out var query))
            _shared[key] = query = template.For(ids, Scope(scoped));
        return query;
    }

    // The entities within the request's scope: tuples of the scope relation on the request's entity
    // type whose direct subject is the scope subject. Read once per request.
    private LookupEntityQuery? ScopeIds(bool scoped)
    {
        if (!scoped || request.Scope is not { } scope) return null;
        return _scopeIds ??= new LookupEntityQuery
        {
            Kind = LookupEntityQueryKind.Relations, EntityType = request.EntityType, Relation = scope.Relation,
            SubjectType = scope.SubjectType, Ids = [scope.SubjectId],
        };
    }

    private readonly record struct SharedKey(LookupEntityQueryKind Kind, string EntityType, string Relation,
        string Names, string SubjectType, string SubjectRelation, string SubEntityType, string SubRelation, bool Scoped);
}
