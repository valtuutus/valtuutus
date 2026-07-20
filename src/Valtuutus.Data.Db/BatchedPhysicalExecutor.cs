using System.Data.Common;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.Db;

// A wave's ops packed into one physical DbBatch round trip when the registered reader supports
// IRelationalBatchOps, instead of DefaultPhysicalExecutor's one-round-trip-per-op. Ops this class
// can't batch (wrong provider, or an op kind/ICheckOp with no batch seam) fall back to the exact
// same individual-execution path DefaultPhysicalExecutor uses (PhysicalOpRunner) so behavior for
// the unbatchable case is identical, never a second implementation to keep in sync.
internal sealed class BatchedPhysicalExecutor(Schema schema) : IPhysicalExecutor
{
    // Property (not a field), same reasoning as DefaultPhysicalExecutor.Reader: set-only interface
    // member, implicit implementations must be public even on an internal class/interface pair.
    public IDataReaderProvider Reader { get; set; } = null!;

    public void Submit(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
    {
        // A wave of 0-1 ops has nothing to gain from batching — one command is one round trip
        // either way, and building a DbBatch/DbBatchCommand object graph for it costs more than
        // the plain single-command path (measured: this fast path exists because the DbBatch-Task 9
        // benchmarks showed a universal latency regression against a local Postgres before it did).
        if (ops.Length <= 1 || Reader is not IRelationalBatchOps batchOps)
        {
            SubmitAllIndividually(ops, ctx, sink, ct);
            return;
        }

        // A ReadOnlySpan can't be captured across an await, so the wave is materialized into an
        // array before handing off to the async continuation.
        _ = RunBatchAsync(ops.ToArray(), ctx, sink, batchOps, ct);
    }

    private void SubmitAllIndividually(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
    {
        foreach (var op in ops)
            _ = PhysicalOpRunner.RunAsync(op, Reader, schema, ctx, sink, ct);
    }

    private async Task RunBatchAsync(PendingOp[] ops, CheckRequestContext ctx, IOpCompletionSink sink,
        IRelationalBatchOps batchOps, CancellationToken ct)
    {
        List<PendingOp>? batchable = null;
        foreach (var op in ops)
        {
            if (IsBatchable(op))
            {
                batchable ??= new List<PendingOp>(ops.Length);
                batchable.Add(op);
            }
            else
            {
                // Fired before the batch is awaited so unbatchable ops in a mixed wave aren't
                // held up behind the batch's single round trip.
                _ = PhysicalOpRunner.RunAsync(op, Reader, schema, ctx, sink, ct);
            }
        }

        // Nothing to batch (either the wave was entirely unbatchable, or empty) — skip the batch
        // machinery entirely rather than dispatch an empty DbBatch.
        if (batchable is null) return;

        // Only one op survived partitioning (the rest of the wave was unbatchable) — same
        // single-op case Submit's fast path handles, just discovered after partitioning instead
        // of before it. Same reasoning: a DbBatch of one command costs more than dispatching it
        // directly.
        if (batchable.Count == 1)
        {
            _ = PhysicalOpRunner.RunAsync(batchable[0], Reader, schema, ctx, sink, ct);
            return;
        }

        var batch = batchOps.CreateBatch();
        foreach (var op in batchable)
            AddToBatch(batch, batchOps, op, ctx);

        // How far into `batchable` a result has already been reported to the sink. A fault at any
        // point after this — obtaining the reader, advancing to a result set, or reading rows out
        // of it — corrupts reader positioning for every command still ahead of it, so everything
        // from this point on (not just the op that threw) must be failed, never left dangling.
        var reported = 0;
        try
        {
            await using var reader = await batchOps.ExecuteBatchAsync(batch, ct).ConfigureAwait(false);
            for (var i = 0; i < batchable.Count; i++)
            {
                if (i > 0)
                    await reader.NextResultAsync(ct).ConfigureAwait(false);
                await ReadResult(reader, batchable[i], sink, ct).ConfigureAwait(false);
                reported = i + 1;
            }
        }
        catch (Exception e)
        {
            for (var i = reported; i < batchable.Count; i++)
                sink.Fail(batchable[i].Token, e);
        }
    }

    private static bool IsBatchable(in PendingOp op) => op.Kind switch
    {
        OpKind.HasDirectRelation or OpKind.HasTrueBoolAttribute or OpKind.TtuFastPath
            or OpKind.HasAnyDirectRelation or OpKind.GetRelations or OpKind.GetIndirectRelations => true,
        // Opaque to the executor by design (Rewriting.cs's ICheckOp contract) — only a rewriter-
        // installed op that also declares IBatchableCheckOp participates in the shared batch.
        OpKind.CheckOp => op.Op is IBatchableCheckOp,
        _ => false,
    };

    private static void AddToBatch(DbBatch batch, IRelationalBatchOps batchOps, in PendingOp op, CheckRequestContext ctx)
    {
        switch (op.Kind)
        {
            case OpKind.HasDirectRelation:
                batchOps.AddHasDirectRelationToBatch(batch, Filter(op, ctx), ctx.SubjectId!);
                break;
            case OpKind.HasTrueBoolAttribute:
                batchOps.AddHasTrueBoolAttributeToBatch(batch, op.EntityType, op.EntityId, op.Relation!, ctx.SnapToken);
                break;
            case OpKind.TtuFastPath:
                batchOps.AddHasTupleToUserSetRelationToBatch(batch, op.EntityType, op.EntityId, op.Relation!,
                    op.SubEntityType!, op.ComputedRelation!, ctx.SubjectType!, ctx.SubjectId!, ctx.SnapToken);
                break;
            case OpKind.HasAnyDirectRelation:
                batchOps.AddHasAnyDirectRelationToBatch(batch, op.EntityType, op.EntityIds!, op.Relation!,
                    ctx.SubjectId!, ctx.SnapToken);
                break;
            case OpKind.GetRelations:
                batchOps.AddGetRelationsToBatch(batch, Filter(op, ctx));
                break;
            case OpKind.GetIndirectRelations:
                batchOps.AddGetIndirectRelationsToBatch(batch, Filter(op, ctx));
                break;
            case OpKind.CheckOp:
                ((IBatchableCheckOp)op.Op!).AddToBatch(batch, batchOps, ctx, op.EntityType, op.EntityId);
                break;
            default:
                throw new InvalidOperationException($"Unexpected batchable op kind {op.Kind}");
        }
    }

    private static async Task ReadResult(DbDataReader reader, PendingOp op, IOpCompletionSink sink, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case OpKind.HasDirectRelation:
            case OpKind.HasTrueBoolAttribute:
            case OpKind.TtuFastPath:
            case OpKind.HasAnyDirectRelation:
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false) && reader.GetBoolean(0);
                sink.Complete(op.Token, result);
                break;
            }
            case OpKind.GetRelations:
            case OpKind.GetIndirectRelations:
            {
                // Same 6-column shape as PostgresDataReaderProvider.ReadRelationTuple / the
                // non-batch GetRelations/GetIndirectRelations methods.
                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        pooled.Add(new RelationTuple(
                            reader.GetString(0), reader.GetString(1), reader.GetString(2),
                            reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                    }
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
                sink.CompleteWithPayload(op.Token, pooled);
                break;
            }
            case OpKind.CheckOp:
                // Reads its own rows and reports to the sink itself.
                await ((IBatchableCheckOp)op.Op!).ReadResultAsync(reader, op.Token, sink, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unexpected batchable op kind {op.Kind}");
        }
    }

    private static RelationTupleFilter Filter(in PendingOp op, CheckRequestContext ctx) => new()
    {
        EntityType = op.EntityType,
        EntityId = op.EntityId,
        Relation = op.Relation!,
        SnapToken = ctx.SnapToken
    };
}
