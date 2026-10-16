using System.Diagnostics;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// LookupSubject as a graph advanced in steps: compiled plans decide what to query, the graph
/// builder binds them to the request, and each step hands every query the graph waits on to the
/// query runner at once.
/// </summary>
internal sealed class LookupSubjectEngineV2(Schema schema, LookupSubjectPlanCache plans, ILookupSubjectQueryRunner runner)
    : ILookupSubjectEngine
{
    public async ValueTask<LookupSubjectResult> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken)
    {
        // Skip the tags iterator allocation entirely when nothing is listening.
        using var activity = DefaultActivitySource.Instance.HasListeners()
            ? DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateSpanAttributes(req))
            : null;

        if (!schema.CanSubjectTypeReach(req.EntityType, req.Permission, req.SubjectType))
            return new LookupSubjectResult { SubjectIds = [], SnapToken = req.SnapToken };

        var root = new LookupSubjectGraphBuilder(plans, req)
            .Build(req.EntityType, req.Permission, [req.EntityId], req.Depth);

        var snapToken = req.SnapToken;
        using var pending = new PendingQueries();
        while (!root.Advance(pending))
        {
            if (pending.Count == 0)
                throw new InvalidOperationException("The lookup graph is waiting without any query to run.");
            snapToken = await runner.RunAsync(pending.Queries, snapToken, cancellationToken);
            pending.Answered();
        }

        HashSet<string> result = root.Result!.IsGate || root.Result.Ids!.Count == 0
            ? new HashSet<string>()
            : root.Result.Ids;

        activity?.AddEvent(new ActivityEvent("LookupSubjectResult",
            tags: new ActivityTagsCollection(CreateResultAttributes(result))));
        return new LookupSubjectResult { SubjectIds = result, SnapToken = snapToken };
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateResultAttributes(HashSet<string> result)
    {
        yield return new KeyValuePair<string, object?>("LookupSubjectResultCount", result.Count);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateSpanAttributes(LookupSubjectRequest req)
    {
        yield return new KeyValuePair<string, object?>("LookupSubjectRequest", req);
    }
}
