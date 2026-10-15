using Valtuutus.Core.Pools;

namespace Valtuutus.Core.Engines.LookupEntity.V2;

/// <summary>
/// The queries of one step, each listed once. A step's answers stay readable through the advance
/// that follows it, which reads every one of them; their relation-tuple lists then go back to the pool.
/// </summary>
internal sealed class PendingQueries : IDisposable
{
    private List<LookupEntityQuery> _queries = ListPool<LookupEntityQuery>.Rent();
    private List<LookupEntityQuery> _answered = ListPool<LookupEntityQuery>.Rent();

    public IReadOnlyList<LookupEntityQuery> Queries => _queries;
    public int Count => _queries.Count;

    public void Add(LookupEntityQuery query)
    {
        if (query.Queued) return;
        query.Queued = true;
        _queries.Add(query);
    }

    /// <summary>
    /// Marks the current step answered. The previous step's answers were read by the advance that
    /// queued this one, so they are released here; a released query keeps no answer.
    /// </summary>
    public void Answered()
    {
        Release(_answered);
        (_queries, _answered) = (_answered, _queries);
    }

    public void Dispose()
    {
        Release(_answered);
        Release(_queries);
        ListPool<LookupEntityQuery>.Return(_answered);
        ListPool<LookupEntityQuery>.Return(_queries);
    }

    private static void Release(List<LookupEntityQuery> queries)
    {
        foreach (var query in queries)
        {
            if (query.Result is List<RelationTuple> tuples)
            {
                ListPool<RelationTuple>.Return(tuples);
                query.Result = null;
            }
        }
        queries.Clear();
    }
}

internal abstract class EntityNode
{
    // Shared across requests: no union or intersection ever takes it over.
    public static readonly HashSet<string> NoEntities = new(0);
    public static readonly EntityNode None = new DoneNode();

    /// <summary>The ids of the entities the node grants; read only by its parent once set.</summary>
    public HashSet<string>? Result { get; protected set; }

    /// <summary>
    /// Moves the node forward with the query answers available so far, adding the queries it
    /// still waits on to <paramref name="pending"/>. True once <see cref="Result"/> is set.
    /// </summary>
    public bool Advance(PendingQueries pending) => Result is not null || TryAdvance(pending);

    protected abstract bool TryAdvance(PendingQueries pending);

    // Every node advances, so all of their queries land in the same step.
    protected static bool AdvanceAll(EntityNode[] nodes, PendingQueries pending)
    {
        var done = true;
        foreach (var node in nodes)
            done &= node.Advance(pending);
        return done;
    }

    // A done child's set is read only by its parent, so a union or intersection takes one child's
    // set over and folds the others into it. NoEntities is never taken: an empty child is skipped by
    // a union and ends an intersection.
    protected static HashSet<string> Union(EntityNode[] children)
    {
        HashSet<string>? largest = null;
        foreach (var child in children)
        {
            if (child.Result!.Count > (largest?.Count ?? 0))
                largest = child.Result;
        }
        if (largest is null) return NoEntities;
        foreach (var child in children)
        {
            if (child.Result != largest)
                largest.UnionWith(child.Result!);
        }
        return largest;
    }

    protected static HashSet<string> Intersect(EntityNode[] children)
    {
        HashSet<string>? taken = null;
        foreach (var child in children)
        {
            if (child.Result!.Count == 0) return NoEntities;
            if (taken is null) taken = child.Result;
            else
            {
                taken.IntersectWith(child.Result);
                if (taken.Count == 0) return NoEntities;
            }
        }
        return taken ?? NoEntities;
    }

    // Answers are released after the advance that follows them, so a node keeps only copied ids.
    protected static bool TryReadIds(LookupEntityQuery query, PendingQueries pending, ref HashSet<string>? ids)
    {
        if (ids is not null) return true;
        if (query.Result is null)
        {
            pending.Add(query);
            return false;
        }
        ids = AnswerPlan.EntityIds((List<RelationTuple>)query.Result);
        return true;
    }

    private sealed class DoneNode : EntityNode
    {
        public DoneNode() => Result = NoEntities;
        protected override bool TryAdvance(PendingQueries pending) => true;
    }
}

/// <summary>
/// A node answered by one query. Several nodes may share a query (sibling relations fetched
/// together, or the same data-independent read reached twice); each evaluates its own part.
/// </summary>
internal sealed class QueryNode(LookupEntityQuery query, AnswerPlan plan, LookupEntityRequest request) : EntityNode
{
    protected override bool TryAdvance(PendingQueries pending)
    {
        if (query.Result is null)
        {
            pending.Add(query);
            return false;
        }
        Result = plan.Evaluate(query.Result, request);
        return true;
    }
}

/// <summary>
/// A dependent hop: once the child lookup on the other entity type is known, one query reads the
/// tuples whose subjects are the entities it found.
/// </summary>
internal sealed class ExpandNode(EntityNode child, LookupEntityQuery main, EntityScope? scope) : EntityNode
{
    private LookupEntityQuery? _main;

    protected override bool TryAdvance(PendingQueries pending)
    {
        if (_main is null)
        {
            if (!child.Advance(pending)) return false;
            if (child.Result!.Count == 0)
            {
                Result = NoEntities;
                return true;
            }
            _main = main.For(child.Result.ToArray(), scope);
        }
        if (_main.Result is null)
        {
            pending.Add(_main);
            return false;
        }
        Result = AnswerPlan.EntityIds((List<RelationTuple>)_main.Result);
        return true;
    }
}

internal sealed class SetNode(EntityNode[] children, bool union) : EntityNode
{
    protected override bool TryAdvance(PendingQueries pending)
    {
        if (!AdvanceAll(children, pending)) return false;
        Result = union ? Union(children) : Intersect(children);
        return true;
    }
}

/// <summary>
/// Attribute expressions checked against the other children's common entities: once those are
/// known, each expression reads its attributes for them only.
/// </summary>
internal sealed class ConstrainedIntersectNode(EntityNode[] others, ExpressionPlan[] checks, LookupEntityRequest request)
    : EntityNode
{
    private LookupEntityQuery[]? _checks;

    protected override bool TryAdvance(PendingQueries pending)
    {
        if (_checks is null)
        {
            if (!AdvanceAll(others, pending)) return false;
            var candidates = Intersect(others);
            if (candidates.Count == 0)
            {
                Result = NoEntities;
                return true;
            }

            var queries = new LookupEntityQuery[checks.Length];
            for (var i = 0; i < checks.Length; i++)
            {
                // An expression whose context is incomplete grants nothing, which empties the intersection.
                if (!checks[i].Expression.IsContextValid(request.Context))
                {
                    Result = NoEntities;
                    return true;
                }
                queries[i] = new LookupEntityQuery
                {
                    Kind = LookupEntityQueryKind.AttributesWithIds, EntityType = checks[i].Template.EntityType,
                    Names = checks[i].Template.Names, Ids = candidates,
                };
            }
            _checks = queries;
        }

        var ready = true;
        foreach (var query in _checks)
        {
            if (query.Result is not null) continue;
            pending.Add(query);
            ready = false;
        }
        if (!ready) return false;

        // Each check reads attributes of the candidates only, so its matches are already candidates.
        var matches = checks[0].Evaluate(_checks[0].Result!, request);
        for (var i = 1; i < _checks.Length && matches.Count > 0; i++)
            matches.IntersectWith(checks[i].Evaluate(_checks[i].Result!, request));
        Result = matches.Count == 0 ? NoEntities : matches;
        return true;
    }
}

/// <summary>The positive children's common entities minus everything a negated child grants.</summary>
internal sealed class NegateIntersectNode(EntityNode[] positives, EntityNode[] negated) : EntityNode
{
    protected override bool TryAdvance(PendingQueries pending)
    {
        var positivesDone = AdvanceAll(positives, pending);
        var negatedDone = AdvanceAll(negated, pending);
        if (!positivesDone || !negatedDone) return false;

        var kept = Intersect(positives);
        if (kept.Count == 0)
        {
            Result = NoEntities;
            return true;
        }
        foreach (var node in negated)
            kept.ExceptWith(node.Result!);
        Result = kept.Count == 0 ? NoEntities : kept;
        return true;
    }
}

/// <summary>
/// Every entity of the type the child does not grant: once the child is known, one more query
/// lists the rest. Within a scope, only the scope's entities are kept.
/// </summary>
internal sealed class NegateNode(EntityNode child, string entityType, LookupEntityQuery? scopeIds) : EntityNode
{
    private HashSet<string>? _scope;
    private LookupEntityQuery? _complement;

    protected override bool TryAdvance(PendingQueries pending)
    {
        var childDone = child.Advance(pending);
        if (scopeIds is not null)
        {
            if (!TryReadIds(scopeIds, pending, ref _scope)) return false;
            if (_scope!.Count == 0)
            {
                Result = NoEntities;
                return true;
            }
        }
        if (!childDone) return false;

        _complement ??= new LookupEntityQuery
        {
            Kind = LookupEntityQueryKind.EntitiesExcluding, EntityType = entityType, Ids = child.Result!,
        };
        if (_complement.Result is null)
        {
            pending.Add(_complement);
            return false;
        }

        var result = new HashSet<string>();
        foreach (var id in (List<string>)_complement.Result)
        {
            if (_scope is null || _scope.Contains(id))
                result.Add(id);
        }
        Result = result;
        return true;
    }
}

/// <summary>A boolean attribute: the entities where it is true, within the scope's entities when scoped.</summary>
internal sealed class AttributeNode(LookupEntityQuery query, LookupEntityQuery? scopeIds) : EntityNode
{
    private HashSet<string>? _scope;

    protected override bool TryAdvance(PendingQueries pending)
    {
        var ready = true;
        if (query.Result is null)
        {
            pending.Add(query);
            ready = false;
        }
        if (scopeIds is not null && !TryReadIds(scopeIds, pending, ref _scope)) ready = false;
        if (!ready) return false;

        if (_scope is { Count: 0 })
        {
            Result = NoEntities;
            return true;
        }
        var result = new HashSet<string>();
        foreach (var attribute in (List<AttributeTuple>)query.Result!)
        {
            if ((_scope is null || _scope.Contains(attribute.EntityId))
                && attribute.Value.TryGetValue(out bool value) && value)
                result.Add(attribute.EntityId);
        }
        Result = result;
        return true;
    }
}
