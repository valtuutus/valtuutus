using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal sealed class CheckEngineV2(IDataReaderProvider reader, Schema schema, CheckPlanExecutorPool executorPool,
    CombinedPlanCache combinedPlans)
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
        var results = await executor.ExecuteSingleAsync(
            new CheckRootRequest(req.EntityType, req.EntityId, req.Permission, req.SubjectRelation, req.Depth),
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
            SnapToken = snapToken, Context = req.Context
        };

        var permissions = schema.GetPermissions(req.EntityType);

        // Roots buffer is rented from the executor itself (reused across calls on this pooled
        // instance — see CheckPlanExecutor.RentMultiRootBuffer), not a fresh `new
        // CheckRootRequest[permissions.Length]` every call. Rent the executor before filling it:
        // the buffer is owned by the executor instance, so it must be picked first.
        var executor = executorPool.Rent(reader);
        var roots = executor.RentMultiRootBuffer(permissions.Length);
        var rootsSpan = roots.Span;
        var i = 0;
        foreach (var perm in permissions)
            rootsSpan[i++] = new CheckRootRequest(req.EntityType, req.EntityId, perm.Name, null, req.Depth);

        // Combined plan: all N permission trees hash-consed together (cached per
        // (EntityType, SubjectType)), so a subtree shared across two permissions gets one slot
        // in one shared array below instead of one array per root. Roots is index-aligned with
        // combined.Roots and (below) with permissions itself, because this loop, CombinedPlanCache,
        // and the result-building loop below all iterate the same schema.GetPermissions(entityType)
        // FrozenDictionary.Values, whose iteration order is fixed once the schema is built.
        var combined = combinedPlans.GetOrCompile(req.EntityType, ctx.SubjectType);

        // One driver loop, N roots: all permissions evaluate concurrently and share the
        // request's dynamic memo — the V2 equivalent of V1's shared CheckMemo here.
        var results = await executor.ExecuteAsync(roots, ctx, cancellationToken,
            precompiledRoots: combined.Roots, combinedSlotCount: combined.SlotCount);
        _ = DrainAndReturn(executor);

        // Re-enumerate permissions (instead of a `names[]` array built alongside roots above) to
        // pair each name with its result — avoids a second heap array purely to carry names
        // across the await.
        var dict = new Dictionary<string, bool>(permissions.Length);
        var j = 0;
        foreach (var perm in permissions)
            dict[perm.Name] = results[j++];
        return dict;
    }

    public async Task<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken)
    {
        var snapToken = await SnapTokenUtils.ResolveLatest(reader, req.SnapToken, cancellationToken);
        var ctx = new CheckRequestContext
        {
            SubjectType = req.SubjectType, SubjectId = req.SubjectId,
            SnapToken = snapToken, Context = req.Context
        };
        var executor = executorPool.Rent(reader);
        // Not the Check()/DrainAndReturn fire-and-forget pattern: ExecuteExplainAsync already
        // awaits DrainStragglersAsync internally before returning (see its own comment for why),
        // so by this point the executor is fully quiesced and safe to return synchronously.
        var (result, root) = await executor.ExecuteExplainAsync(
            new CheckRootRequest(req.EntityType, req.EntityId, req.Permission, req.SubjectRelation, req.Depth),
            ctx, cancellationToken);
        executorPool.Return(executor);
        return new CheckExplainResult { Result = result, Root = root };
    }
}
