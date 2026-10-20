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
    public async ValueTask<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        using var trace = ValtuutusTelemetry.StartRequest(TelemetryOperation.LookupEntity, req.SnapToken);
        if (trace is not null)
        {
            trace.SetTag(ValtuutusTelemetry.EntityType, req.EntityType);
            trace.SetTag(ValtuutusTelemetry.Permission, req.Permission);
            trace.SetTag(ValtuutusTelemetry.SubjectType, req.SubjectType);
            trace.SetTag(ValtuutusTelemetry.SubjectId, req.SubjectId);
        }

        try
        {
            if (req.Scope is { } scope && schema.GetRelationType(req.EntityType, scope.Relation) == RelationType.None)
                throw new InvalidOperationException(
                    $"Scope relation '{scope.Relation}' does not exist on entity type '{req.EntityType}'.");

            if (!schema.CanSubjectTypeReach(req.EntityType, req.Permission, req.SubjectType))
            {
                trace?.Answer(req.SnapToken);
                trace?.SetTag(ValtuutusTelemetry.ResultCount, 0);
                return new LookupEntityPage([], null) { SnapToken = req.SnapToken };
            }

            var builder = new LookupEntityGraphBuilder(plans, req);
            List<string> sorted;
            var snapToken = req.SnapToken;
            try
            {
                var root = builder.Build(req.EntityType, req.Permission, req.Depth, scoped: req.Scope is not null);

                using (var pending = new PendingQueries())
                {
                    while (!root.Advance(pending))
                    {
                        if (pending.Count == 0)
                            throw new InvalidOperationException("The lookup graph is waiting without any query to run.");
                        trace?.NextStep();
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

            trace?.Answer(snapToken);
            trace?.SetTag(ValtuutusTelemetry.ResultCount, sorted.Count);

            return Page(sorted, req.PageSize, req.ContinuationToken) with { SnapToken = snapToken };
        }
        catch (Exception e) when (trace is not null && trace.Fail(e))
        {
            throw;
        }
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
}
