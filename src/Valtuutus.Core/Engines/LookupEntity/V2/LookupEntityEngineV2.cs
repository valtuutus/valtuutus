using System.Diagnostics;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupEntity.V2;

/// <summary>
/// LookupEntity as a graph advanced in steps: compiled plans decide what to query, the graph builder
/// binds them to the request, and each step hands every query the graph waits on to the query
/// runner at once.
/// </summary>
internal sealed class LookupEntityEngineV2(Schema schema, LookupEntityPlanCache plans, ILookupEntityQueryRunner runner)
    : ILookupEntityEngine
{
    public async Task<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        // Skip the tags iterator allocation entirely when nothing is listening.
        using var activity = DefaultActivitySource.Instance.HasListeners()
            ? DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateSpanAttributes(req))
            : null;

        if (req.Scope is { } scope && schema.GetRelationType(req.EntityType, scope.Relation) == RelationType.None)
            throw new InvalidOperationException(
                $"Scope relation '{scope.Relation}' does not exist on entity type '{req.EntityType}'.");

        if (!schema.CanSubjectTypeReach(req.EntityType, req.Permission, req.SubjectType))
            return new LookupEntityPage([], null);

        var builder = new LookupEntityGraphBuilder(plans, req);
        List<string> sorted;
        try
        {
            var root = builder.Build(req.EntityType, req.Permission, req.Depth, scoped: req.Scope is not null);

            var snapToken = req.SnapToken;
            using (var pending = new PendingQueries())
            {
                while (!root.Advance(pending))
                {
                    if (pending.Count == 0)
                        throw new InvalidOperationException("The lookup graph is waiting without any query to run.");
                    snapToken = await runner.RunAsync(pending.Queries, snapToken, cancellationToken);
                    pending.Answered();
                }
            }

            sorted = new List<string>(root.Result!);
            EntitySets.Return(root.Result);
        }
        finally
        {
            builder.Release();
        }
        sorted.Sort(StringComparer.Ordinal);

        activity?.AddEvent(new ActivityEvent("LookupEntityResult",
            tags: new ActivityTagsCollection(CreateResultAttributes(sorted.Count))));

        return Page(sorted, req.PageSize, req.ContinuationToken);
    }

    // Pages are cut from the ordinal-sorted ids; the continuation token is the base64 of the last id served.
    private static LookupEntityPage Page(List<string> sorted, int? pageSize, string? continuationToken)
    {
        if (pageSize is not { } size)
            return new LookupEntityPage(sorted, null);

        string? afterId = null;
        if (!string.IsNullOrEmpty(continuationToken))
            afterId = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken));

        var startIndex = 0;
        if (afterId is not null)
        {
            var idx = sorted.BinarySearch(afterId, StringComparer.Ordinal);
            startIndex = idx >= 0 ? idx + 1 : ~idx;
        }

        var page = sorted.GetRange(startIndex, Math.Min(size, sorted.Count - startIndex));
        string? nextToken = null;
        if (startIndex + size < sorted.Count)
            nextToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(page[^1]));

        return new LookupEntityPage(page, nextToken);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateResultAttributes(int count)
    {
        yield return new KeyValuePair<string, object?>("LookupEntityResultCount", count);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateSpanAttributes(LookupEntityRequest req)
    {
        yield return new KeyValuePair<string, object?>("LookupEntityRequest", req);
    }
}
