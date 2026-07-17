using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal sealed class CheckEngineV2(IDataReaderProvider reader, Schema schema, CheckPlanExecutorPool executorPool)
    : ICheckEngine
{
    public async Task<bool> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        ValtuutusMetrics.CheckRequests.Add(1);
        var snapToken = await SnapTokenUtils.ResolveLatest(reader, req.SnapToken, cancellationToken);
        var ctx = new CheckRequestContext
        {
            SubjectType = req.SubjectType, SubjectId = req.SubjectId,
            SnapToken = snapToken, Context = req.Context
        };
        var executor = executorPool.Rent(reader);
        var results = await executor.ExecuteAsync(
            [new CheckRootRequest(req.EntityType, req.EntityId, req.Permission, req.SubjectRelation, req.Depth)],
            ctx, cancellationToken, memoizeRoots: false);
        // Reached only on success (an exception here leaves `executor` un-pooled — safe, just
        // sacrifices reuse of that one instance). Fire-and-forget: a short-circuited Union may
        // still have losing siblings in flight, and draining them must not add latency to an
        // answer we already have — see DrainStragglersAsync for why waiting matters at all.
        _ = DrainAndReturn(executor);
        return results[0];
    }

    private async Task DrainAndReturn(CheckPlanExecutor executor)
    {
        try
        {
            await executor.DrainStragglersAsync().ConfigureAwait(false);
            executorPool.Return(executor);
        }
        catch
        {
            // Draining itself faulted — this instance's straggler accounting is now unreliable,
            // so it's safer to let it be collected than risk pooling it in an unverified state.
        }
    }

    public async Task<Dictionary<string, bool>> SubjectPermission(SubjectPermissionRequest req,
        CancellationToken cancellationToken)
    {
        ValtuutusMetrics.CheckRequests.Add(1);
        var snapToken = await SnapTokenUtils.ResolveLatest(reader, req.SnapToken, cancellationToken);
        var ctx = new CheckRequestContext
        {
            SubjectType = req.SubjectType, SubjectId = req.SubjectId,
            SnapToken = snapToken, Context = new Dictionary<string, object>(0)
        };

        var permissions = schema.GetPermissions(req.EntityType);
        var roots = new CheckRootRequest[permissions.Count];
        var names = new string[permissions.Count];
        var i = 0;
        foreach (var perm in permissions)
        {
            names[i] = perm.Name;
            roots[i] = new CheckRootRequest(req.EntityType, req.EntityId, perm.Name, null, req.Depth);
            i++;
        }

        // One driver loop, N roots: all permissions evaluate concurrently and share the
        // request's dynamic memo — the V2 equivalent of V1's shared CheckMemo here.
        var executor = executorPool.Rent(reader);
        var results = await executor.ExecuteAsync(roots, ctx, cancellationToken);
        _ = DrainAndReturn(executor);

        var dict = new Dictionary<string, bool>(names.Length);
        for (var j = 0; j < names.Length; j++)
            dict[names[j]] = results[j];
        return dict;
    }

    // Explain stays on V1 for now (design doc: "V1 keeps serving Explain initially").
    public Task<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken)
        => new CheckEngine(reader, schema).Explain(req, cancellationToken);
}
