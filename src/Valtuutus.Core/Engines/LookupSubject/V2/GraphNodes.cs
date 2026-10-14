using System.Runtime.InteropServices;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// What a graph node evaluates to: the ids of the subjects it grants, or, for an attribute leaf,
/// whether the entity's attributes matched. A gate grants no subjects of its own: an unmatched
/// gate empties an enclosing intersection, a matched one lets it through, and a union ignores both.
/// </summary>
internal sealed class SubjectSet
{
    // Shared and never mutated: every combination below builds a new set.
    public static readonly SubjectSet Empty = new(new HashSet<string>(0), isGate: false, matched: false);
    private static readonly SubjectSet MatchedGate = new(null, isGate: true, matched: true);
    private static readonly SubjectSet UnmatchedGate = new(null, isGate: true, matched: false);

    private SubjectSet(HashSet<string>? ids, bool isGate, bool matched)
    {
        Ids = ids;
        IsGate = isGate;
        Matched = matched;
    }

    // Null for a gate.
    public HashSet<string>? Ids { get; }
    public bool IsGate { get; }
    public bool Matched { get; }

    public static SubjectSet Of(HashSet<string> ids) => new(ids, isGate: false, matched: false);

    public static SubjectSet Gate(bool matched) => matched ? MatchedGate : UnmatchedGate;

    public static SubjectSet FromTuples(List<RelationTuple> tuples, string? relation = null)
    {
        var ids = new HashSet<string>();
        foreach (ref readonly var tuple in CollectionsMarshal.AsSpan(tuples))
        {
            if (relation is null || tuple.Relation == relation)
                ids.Add(tuple.SubjectId);
        }
        return Of(ids);
    }

    public static SubjectSet Union(GraphNode[] children)
    {
        var ids = new HashSet<string>();
        foreach (var child in children)
        {
            if (!child.Result!.IsGate)
                ids.UnionWith(child.Result.Ids!);
        }
        return Of(ids);
    }

    public static SubjectSet Intersect(GraphNode[] children)
    {
        HashSet<string>? ids = null;
        foreach (var child in children)
        {
            var result = child.Result!;
            if (result.IsGate)
            {
                if (!result.Matched) return Empty;
                continue;
            }

            if (ids is null) ids = new HashSet<string>(result.Ids!);
            else ids.IntersectWith(result.Ids!);
            if (ids.Count == 0) return Empty;
        }
        return ids is null ? Empty : Of(ids);
    }
}

/// <summary>The queries of one step, each listed once.</summary>
internal sealed class PendingQueries
{
    private readonly List<LookupSubjectQuery> _queries = [];

    public IReadOnlyList<LookupSubjectQuery> Queries => _queries;
    public int Count => _queries.Count;

    public void Add(LookupSubjectQuery query)
    {
        if (query.Queued) return;
        query.Queued = true;
        _queries.Add(query);
    }

    public void Clear() => _queries.Clear();
}

internal abstract class GraphNode
{
    public static readonly GraphNode None = new DoneNode(SubjectSet.Empty);

    public SubjectSet? Result { get; protected set; }

    /// <summary>
    /// Moves the node forward with the query answers available so far, adding the queries it
    /// still waits on to <paramref name="pending"/>. True once <see cref="Result"/> is set.
    /// </summary>
    public bool Advance(PendingQueries pending) => Result is not null || TryAdvance(pending);

    protected abstract bool TryAdvance(PendingQueries pending);

    private sealed class DoneNode : GraphNode
    {
        public DoneNode(SubjectSet result) => Result = result;
        protected override bool TryAdvance(PendingQueries pending) => true;
    }
}

/// <summary>
/// A node answered by one query. Several nodes may share a query (sibling relations fetched
/// together); each evaluates its own part of the answer.
/// </summary>
internal sealed class QueryNode(LookupSubjectQuery query, AnswerPlan plan, LookupSubjectRequest request) : GraphNode
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
/// A dependent hop: the query finds the intermediate subjects, and the lookup continues from
/// them through the hop's target, built once their ids are known.
/// </summary>
internal sealed class ExpandNode(LookupSubjectQuery query, ExpandPlan plan, int depth, LookupSubjectGraphBuilder builder)
    : GraphNode
{
    private GraphNode? _continuation;

    protected override bool TryAdvance(PendingQueries pending)
    {
        if (_continuation is null)
        {
            if (query.Result is null)
            {
                pending.Add(query);
                return false;
            }
            var ids = SubjectSet.FromTuples((List<RelationTuple>)query.Result).Ids!;
            _continuation = builder.Build(plan.EntityType, plan.Name, ids, depth);
        }

        if (!_continuation.Advance(pending)) return false;
        Result = _continuation.Result;
        return true;
    }
}

internal sealed class SetNode(GraphNode[] children, bool union) : GraphNode
{
    protected override bool TryAdvance(PendingQueries pending)
    {
        // Every child advances, so all of their queries land in the same step.
        var done = true;
        foreach (var child in children)
            done &= child.Advance(pending);
        if (!done) return false;

        Result = union ? SubjectSet.Union(children) : SubjectSet.Intersect(children);
        return true;
    }
}

/// <summary>Subjects the child does not grant: once the child is known, one more query lists the rest.</summary>
internal sealed class NegateNode(GraphNode child, string subjectType) : GraphNode
{
    private LookupSubjectQuery? _complement;

    protected override bool TryAdvance(PendingQueries pending)
    {
        if (!child.Advance(pending)) return false;

        _complement ??= new LookupSubjectQuery
        {
            Kind = LookupSubjectQueryKind.SubjectsExcluding, SubjectType = subjectType,
            Ids = child.Result!.IsGate ? new HashSet<string>() : child.Result.Ids!,
        };
        if (_complement.Result is null)
        {
            pending.Add(_complement);
            return false;
        }
        Result = SubjectSet.Of(new HashSet<string>((List<string>)_complement.Result));
        return true;
    }
}
