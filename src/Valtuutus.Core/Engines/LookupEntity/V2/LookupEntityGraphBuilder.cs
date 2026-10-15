using Valtuutus.Core.Pools;

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

    /// <summary>Returns the request's sharing table to the pool; call once the lookup is done.</summary>
    public void Release()
    {
        if (_shared is null) return;
        DictionaryPool<SharedKey, LookupEntityQuery>.Return(_shared);
        _shared = null;
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
        FixpointPlan fixpoint => Fixpoint(fixpoint, null, depth, scoped),
        FixpointLevelsPlan levels => Fixpoint(levels.OneLevel, levels, depth, scoped),
        SetPlan set => new SetNode(Children(set.Children, set.Shared, depth, scoped), set.Union),
        ConstrainedIntersectPlan constrained => new ConstrainedIntersectNode(
            Children(constrained.Others, constrained.Shared, depth, scoped), constrained.Checks, request),
        NegateIntersectPlan negate => new NegateIntersectNode(
            Children(negate.Positives, negate.Shared, depth, scoped), Children(negate.Negated, null, depth, scoped)),
        NegatePlan negate => new NegateNode(Instantiate(negate.Child, depth, scoped), negate.EntityType, ScopeIds(scoped)),
        _ => throw new InvalidOperationException(),
    };

    // Within a scope the root level reads scoped, as an unrolled relation does: its direct and
    // joined reads, and the hop over the unscoped walk one level down.
    private EntityNode Fixpoint(FixpointPlan fixpoint, FixpointLevelsPlan? levels, int depth, bool scoped)
    {
        if (Scope(scoped) is not { } scope) return Walk(fixpoint, levels, depth);

        var nodes = new List<EntityNode>();
        nodes.AddRange(Instantiate(fixpoint.Direct, depth, scoped: true));
        if (depth > 0)
        {
            nodes.AddRange(Instantiate(fixpoint.Joined, depth, scoped: true));
            nodes.Add(new ExpandNode(Walk(fixpoint, levels, depth - 1), fixpoint.Hop, scope));
        }
        return new SetNode(nodes.ToArray(), union: true);
    }

    // A walk with a direct target, no joined grants and a level to spend reads its direct grants as level 0 of its
    // first levels query, when the provider runs that query.
    private FixpointNode Walk(FixpointPlan fixpoint, FixpointLevelsPlan? levels, int depth)
    {
        if (levels?.FromSubject is { } fromSubject && depth > 0 && fixpoint.Joined.Length == 0
            && fixpoint.Direct.Length > 0)
        {
            return new FixpointNode([], [], fixpoint.Hop, depth, levels.Template, levels.Levels,
                fromSubject.For(_subject, null, Math.Min(levels.Levels, depth)));
        }

        return new FixpointNode(
            Instantiate(fixpoint.Direct, depth, scoped: false),
            depth == 0 ? [] : Instantiate(fixpoint.Joined, depth, scoped: false),
            fixpoint.Hop, depth, levels?.Template, levels?.Levels ?? 1);
    }

    private EntityNode[] Instantiate(SubjectBoundPlan[] plans, int depth, bool scoped)
    {
        var nodes = new EntityNode[plans.Length];
        for (var i = 0; i < nodes.Length; i++)
            nodes[i] = Instantiate(plans[i], depth, scoped);
        return nodes;
    }

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
        var key = new SharedKey(template.Kind, template.EntityType, template.Relation, template.NamesKey,
            template.SubjectType, template.SubjectRelation, template.SubEntityType, template.SubRelation, scoped);
        _shared ??= DictionaryPool<SharedKey, LookupEntityQuery>.Rent();
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
