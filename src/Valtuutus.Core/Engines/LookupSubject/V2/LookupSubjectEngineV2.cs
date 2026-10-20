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
        using var trace = ValtuutusTelemetry.StartRequest(TelemetryOperation.LookupSubject, req.SnapToken);
        if (trace is not null)
        {
            trace.SetTag(ValtuutusTelemetry.EntityType, req.EntityType);
            trace.SetTag(ValtuutusTelemetry.EntityId, req.EntityId);
            trace.SetTag(ValtuutusTelemetry.Permission, req.Permission);
            trace.SetTag(ValtuutusTelemetry.SubjectType, req.SubjectType);
        }

        try
        {
            if (!schema.CanSubjectTypeReach(req.EntityType, req.Permission, req.SubjectType))
            {
                trace?.Answer(req.SnapToken);
                trace?.SetTag(ValtuutusTelemetry.ResultCount, 0);
                return new LookupSubjectResult { SubjectIds = [], SnapToken = req.SnapToken };
            }

            var root = new LookupSubjectGraphBuilder(plans, req)
                .Build(req.EntityType, req.Permission, [req.EntityId], req.Depth);

            var snapToken = req.SnapToken;
            using var pending = new PendingQueries();
            while (!root.Advance(pending))
            {
                if (pending.Count == 0)
                    throw new InvalidOperationException("The lookup graph is waiting without any query to run.");
                trace?.NextStep();
                snapToken = await runner.RunAsync(pending.Queries, snapToken, cancellationToken);
                pending.Answered();
            }

            HashSet<string> result = root.Result!.IsGate || root.Result.Ids!.Count == 0
                ? new HashSet<string>()
                : root.Result.Ids;

            trace?.Answer(snapToken);
            trace?.SetTag(ValtuutusTelemetry.ResultCount, result.Count);
            return new LookupSubjectResult { SubjectIds = result, SnapToken = snapToken };
        }
        catch (Exception e) when (trace is not null && trace.Fail(e))
        {
            throw;
        }
    }
}
