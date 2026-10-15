using Valtuutus.Core.Pools;

namespace Valtuutus.Core.Engines.LookupEntity.V2;

/// <summary>
/// Entity-id sets rented per request. Each set has one owner at a time, a node, which may lend it
/// to a query's ids while that query's step runs and returns it once done. <see cref="None"/> is
/// shared and never returned; sets above <see cref="MaxPooledCount"/> go to the collector, so the
/// pool keeps only buffers sized for typical results.
/// </summary>
internal static class EntitySets
{
    public static readonly HashSet<string> None = new(0);

    public const int MaxPooledCount = 1024;

    public static HashSet<string> Rent() => HashSetPool<string>.Rent();

    public static void Return(HashSet<string>? set)
    {
        if (set is not null && !ReferenceEquals(set, None) && set.Count <= MaxPooledCount)
            HashSetPool<string>.Return(set);
    }
}

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
    public static HashSet<string> NoEntities => EntitySets.None;
    public static readonly EntityNode None = new DoneNode();

    /// <summary>The ids of the entities the node grants; owned by its parent once set.</summary>
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

    // A union keeps its largest child's set, folds the others into it and returns them.
    protected static HashSet<string> Union(EntityNode[] children)
    {
        HashSet<string>? largest = null;
        foreach (var child in children)
        {
            if (child.Result!.Count > (largest?.Count ?? 0))
                largest = child.Result;
        }
        foreach (var child in children)
        {
            if (child.Result == largest) continue;
            largest?.UnionWith(child.Result!);
            EntitySets.Return(child.Result);
        }
        return largest ?? NoEntities;
    }

    // An intersection keeps its first child's set, narrows it by the others and returns them; an
    // empty child empties it.
    protected static HashSet<string> Intersect(EntityNode[] children)
    {
        HashSet<string>? taken = null;
        var empty = false;
        foreach (var child in children)
        {
            var set = child.Result!;
            if (empty || set.Count == 0)
            {
                empty = true;
                EntitySets.Return(set);
                continue;
            }
            if (taken is null)
            {
                taken = set;
                continue;
            }
            taken.IntersectWith(set);
            EntitySets.Return(set);
            if (taken.Count == 0) empty = true;
        }
        if (!empty && taken is not null) return taken;
        EntitySets.Return(taken);
        return NoEntities;
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
                EntitySets.Return(child.Result);
                Result = NoEntities;
                return true;
            }
            // The query takes a copy of the ids, so the child's set is done.
            _main = main.For(child.Result.ToArray(), scope);
            EntitySets.Return(child.Result);
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

/// <summary>
/// A self-recursive relation walked as a frontier: the entities its direct and joined reads grant,
/// then, one hop per step, the entities whose tuples name the newly found ones through the
/// relation, until a hop adds nothing or the depth is spent. A direct grant costs no level, a
/// joined grant one, and each hop one more.
/// </summary>
internal sealed class FixpointNode(EntityNode[] direct, EntityNode[] joined, LookupEntityQuery hop, int depth)
    : EntityNode
{
    private HashSet<string>? _found;
    private HashSet<string>? _frontier;
    private HashSet<string>? _joined;
    private LookupEntityQuery? _hop;
    private int _hops;

    protected override bool TryAdvance(PendingQueries pending)
    {
        if (_found is null)
        {
            var directDone = AdvanceAll(direct, pending);
            var joinedDone = AdvanceAll(joined, pending);
            if (!directDone || !joinedDone) return false;

            var granted = Union(direct);
            if (depth == 0)
            {
                Result = granted;
                return true;
            }
            _found = EntitySets.Rent();
            _found.UnionWith(granted);
            _frontier = granted.Count == 0 ? EntitySets.Rent() : granted;
            _joined = Union(joined);
        }

        while (_hops < depth)
        {
            HashSet<string> next;
            if (_frontier!.Count > 0)
            {
                // The frontier stays with the hop query until its answer is read.
                _hop ??= hop.For(_frontier, null);
                if (_hop.Result is null)
                {
                    pending.Add(_hop);
                    return false;
                }
                next = AnswerPlan.EntityIds((List<RelationTuple>)_hop.Result);
                _hop = null;
            }
            else
            {
                next = EntitySets.Rent();
            }

            if (_hops == 0)
            {
                next.UnionWith(_joined!);
                EntitySets.Return(_joined);
                _joined = null;
            }
            _hops++;
            next.ExceptWith(_found);
            _found.UnionWith(next);
            EntitySets.Return(_frontier);
            _frontier = next;
            if (next.Count == 0) break;
        }

        EntitySets.Return(_frontier);
        _frontier = null;
        EntitySets.Return(_joined);
        _joined = null;
        if (_found.Count == 0)
        {
            EntitySets.Return(_found);
            Result = NoEntities;
            return true;
        }
        Result = _found;
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
    private HashSet<string>? _candidates;
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

            // An expression whose context is incomplete grants nothing, which empties the intersection.
            foreach (var check in checks)
            {
                if (check.Expression.IsContextValid(request.Context)) continue;
                EntitySets.Return(candidates);
                Result = NoEntities;
                return true;
            }

            // Every check reads the candidates' attributes; the set stays with the queries until
            // their answers are read.
            var queries = new LookupEntityQuery[checks.Length];
            for (var i = 0; i < checks.Length; i++)
            {
                queries[i] = new LookupEntityQuery
                {
                    Kind = LookupEntityQueryKind.AttributesWithIds, EntityType = checks[i].Template.EntityType,
                    Names = checks[i].Template.Names, Ids = candidates,
                };
            }
            _candidates = candidates;
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
        {
            var next = checks[i].Evaluate(_checks[i].Result!, request);
            matches.IntersectWith(next);
            EntitySets.Return(next);
        }
        EntitySets.Return(_candidates);
        _candidates = null;
        if (matches.Count == 0)
        {
            EntitySets.Return(matches);
            Result = NoEntities;
            return true;
        }
        Result = matches;
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
        foreach (var node in negated)
        {
            if (kept.Count > 0) kept.ExceptWith(node.Result!);
            EntitySets.Return(node.Result);
        }
        if (kept.Count == 0)
        {
            EntitySets.Return(kept);
            Result = NoEntities;
            return true;
        }
        Result = kept;
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
                EntitySets.Return(_scope);
                if (childDone) EntitySets.Return(child.Result);
                Result = NoEntities;
                return true;
            }
        }
        if (!childDone) return false;

        // The child's set stays with the complement query until its answer is read.
        _complement ??= new LookupEntityQuery
        {
            Kind = LookupEntityQueryKind.EntitiesExcluding, EntityType = entityType, Ids = child.Result!,
        };
        if (_complement.Result is null)
        {
            pending.Add(_complement);
            return false;
        }

        var result = EntitySets.Rent();
        foreach (var id in (List<string>)_complement.Result)
        {
            if (_scope is null || _scope.Contains(id))
                result.Add(id);
        }
        EntitySets.Return(child.Result);
        EntitySets.Return(_scope);
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
            EntitySets.Return(_scope);
            Result = NoEntities;
            return true;
        }
        var result = EntitySets.Rent();
        foreach (var attribute in (List<AttributeTuple>)query.Result!)
        {
            if ((_scope is null || _scope.Contains(attribute.EntityId))
                && attribute.Value.TryGetValue(out bool value) && value)
                result.Add(attribute.EntityId);
        }
        EntitySets.Return(_scope);
        Result = result;
        return true;
    }
}
