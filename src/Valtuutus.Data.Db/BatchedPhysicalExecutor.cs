using System.Data.Common;
using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.Db;

// A wave's ops packed into one physical DbBatch round trip when the provider registered an
// IRelationalBatchOps (AddDbSetup constructs this executor with whatever IRelationalBatchOps is
// registered — the capability is injected, not discovered by type-testing the reader), instead
// of DefaultPhysicalExecutor's one-round-trip-per-op. A null
// batchOps means the provider has no batch implementation (or opted out) — every wave then takes
// the individual path below. Ops this class can't batch (null batchOps, or an op kind/ICheckOp
// with no batch seam) fall back to the exact same individual-execution path
// DefaultPhysicalExecutor uses (PhysicalOpRunner) so behavior for the unbatchable case is
// identical, never a second implementation to keep in sync.
internal sealed class BatchedPhysicalExecutor(Schema schema, IRelationalBatchOps? batchOps)
    : IPhysicalExecutor
{
    // Property with a getter, same reasoning as DefaultPhysicalExecutor.Reader.
    public IDataReaderProvider Reader { get; set; } = null!;

    public bool CanResolveSnapTokenInFirstRoundTrip => batchOps?.UsingBatchResolvedSnapToken is not null;

    public SnapToken? GetResolvedSnapToken(CheckRequestContext ctx) =>
        ReferenceEquals(_resolvingFor, ctx) && _resolved!.Task.IsCompletedSuccessfully
            ? _resolved.Task.Result.SnapToken
            : null;

    // Instances are rented per request. For a request whose token this executor resolves: the
    // request's unresolved context, and the context carrying the token once its first batch has
    // resolved it. Submit is only ever called by the request's single driver loop.
    private CheckRequestContext? _resolvingFor;
    private TaskCompletionSource<CheckRequestContext>? _resolved;

    public void Submit(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
    {
        if (ctx.ResolveLatestSnapToken)
        {
            // The request's first wave resolves the token inside its own batch — even a single-op
            // wave: two commands in one round trip still beat the dedicated token round trip they
            // replace. Waves submitted before that batch returns wait for the token.
            if (!ReferenceEquals(_resolvingFor, ctx))
            {
                _resolvingFor = ctx;
                _resolved = new TaskCompletionSource<CheckRequestContext>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = RunBatchAsync(ops.ToArray(), ctx, sink, batchOps!, ct, _resolved);
                return;
            }

            var resolved = _resolved!.Task;
            if (!resolved.IsCompletedSuccessfully)
            {
                _ = SubmitWhenResolvedAsync(ops.ToArray(), resolved, sink, ct);
                return;
            }
            ctx = resolved.Result;
        }

        // A wave of 0-1 ops has nothing to gain from batching — one command is one round trip
        // either way, and building a DbBatch/DbBatchCommand object graph for it costs more than
        // the plain single-command path.
        if (ops.Length <= 1 || batchOps is null)
        {
            SubmitAllIndividually(ops, ctx, sink, ct);
            return;
        }

        // A ReadOnlySpan can't be captured across an await, so the wave is materialized into an
        // array before handing off to the async continuation.
        _ = RunBatchAsync(ops.ToArray(), ctx, sink, batchOps, ct, resolving: null);
    }

    private async Task SubmitWhenResolvedAsync(PendingOp[] ops, Task<CheckRequestContext> resolved,
        IOpCompletionSink sink, CancellationToken ct)
    {
        CheckRequestContext ctx;
        try
        {
            ctx = await resolved.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            foreach (var op in ops)
                sink.Fail(op.Token, e);
            return;
        }
        Submit(ops, ctx, sink, ct);
    }

    private void SubmitAllIndividually(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
    {
        foreach (var op in ops)
            _ = PhysicalOpRunner.RunAsync(op, Reader, schema, ctx, sink, ct);
    }

    // `resolving` is non-null for the batch that resolves the request's token: it publishes the
    // context carrying that token there.
    private async Task RunBatchAsync(PendingOp[] ops, CheckRequestContext ctx, IOpCompletionSink sink,
        IRelationalBatchOps batchOps, CancellationToken ct, TaskCompletionSource<CheckRequestContext>? resolving)
    {
        List<PendingOp>? batchable = null;
        // Unbatchable ops of the wave that resolves the token: they need it too, so they run once
        // the batch has resolved it.
        List<PendingOp>? deferred = null;
        foreach (var op in ops)
        {
            // Answered without any fetch — never worth a batch slot.
            if (op.Kind == OpKind.AttributeExpr && !PhysicalOpRunner.IsAttributeExprContextValid(op, ctx))
            {
                sink.Complete(op.Token, false);
                continue;
            }

            if (IsBatchable(op))
            {
                batchable ??= new List<PendingOp>(ops.Length);
                batchable.Add(op);
            }
            else if (resolving is not null)
            {
                (deferred ??= []).Add(op);
            }
            else
            {
                // Fired before the batch is awaited so unbatchable ops in a mixed wave aren't
                // held up behind the batch's single round trip.
                _ = PhysicalOpRunner.RunAsync(op, Reader, schema, ctx, sink, ct);
            }
        }

        if (resolving is null)
        {
            // Nothing to batch (either the wave was entirely unbatchable, or empty) — skip the
            // batch machinery entirely rather than dispatch an empty DbBatch.
            if (batchable is null) return;

            // Only one op survived partitioning (the rest of the wave was unbatchable) — same
            // single-op case Submit's fast path handles, just discovered after partitioning
            // instead of before it. Same reasoning: a DbBatch of one command costs more than
            // dispatching it directly.
            if (batchable.Count == 1)
            {
                _ = PhysicalOpRunner.RunAsync(batchable[0], Reader, schema, ctx, sink, ct);
                return;
            }
        }

        var batchCount = batchable?.Count ?? 0;
        using var span = ValtuutusTelemetry.Enabled
            ? ValtuutusTelemetry.StartRoundTrip(batchOps.DbSystemName, QueryNames(batchable), resolving is not null)
            : default;
        // How far into `batchable` a result has already been reported to the sink. A fault at any
        // point — building the batch, obtaining the reader, advancing to a result set, or reading
        // rows out of it — corrupts reader positioning for every command still ahead of it, so
        // every op from this point on, the one that threw included, is failed and none is left
        // dangling.
        var reported = 0;
        try
        {
            var batch = batchOps.CreateBatch();
            var commandOps = batchOps;
            var buildCtx = ctx;
            if (resolving is not null)
            {
                // Resolver first; every later command comes from the batch-resolved-token catalog,
                // which reads the token the resolver parks instead of binding a parameter — so the
                // context they are built with only needs a placeholder token.
                batchOps.AddResolveLatestSnapTokenToBatch(batch);
                commandOps = batchOps.UsingBatchResolvedSnapToken!;
                buildCtx = WithSnapToken(ctx, SnapToken.MinValue);
            }
            if (batchable is not null)
                foreach (var op in batchable)
                    AddToBatch(batch, commandOps, op, buildCtx);

            await using var reader = await batchOps.ExecuteBatchAsync(batch, ct).ConfigureAwait(false);
            span.Responded();
            if (resolving is not null)
            {
                // Published before any op result: an op completion can make the engine submit the
                // next wave, which must find the token resolved.
                var snapToken = await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(0)
                    ? new SnapToken(reader.GetString(0))
                    : SnapToken.MinValue;
                ctx = WithSnapToken(ctx, snapToken);
                resolving.SetResult(ctx);
                if (deferred is not null)
                    foreach (var op in deferred)
                        _ = PhysicalOpRunner.RunAsync(op, Reader, schema, ctx, sink, ct);
            }

            for (var i = 0; i < batchCount; i++)
            {
                if (i > 0 || resolving is not null)
                    await reader.NextResultAsync(ct).ConfigureAwait(false);
                await ReadResult(reader, batchable![i], ctx, sink, ct).ConfigureAwait(false);
                reported = i + 1;
            }
        }
        catch (Exception e)
        {
            span.RecordFailure(e);
            if (resolving is not null && resolving.TrySetException(e) && deferred is not null)
                foreach (var op in deferred)
                    sink.Fail(op.Token, e);
            for (var i = reported; i < batchCount; i++)
                sink.Fail(batchable![i].Token, e);
        }
    }

    private static CheckRequestContext WithSnapToken(CheckRequestContext ctx, SnapToken snapToken) => new()
    {
        SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId,
        SnapToken = snapToken, Context = ctx.Context
    };

    private static string[] QueryNames(List<PendingOp>? ops)
    {
        if (ops is null) return [];
        var names = new string[ops.Count];
        for (var i = 0; i < names.Length; i++)
            names[i] = ops[i].Kind == OpKind.CheckOp ? ops[i].Op!.GetType().Name : ops[i].Kind.ToString();
        return names;
    }

    private static bool IsBatchable(in PendingOp op) => op.Kind switch
    {
        OpKind.HasDirectRelation or OpKind.HasTrueBoolAttribute or OpKind.TtuFastPath
            or OpKind.HasAnyDirectRelation or OpKind.GetRelations or OpKind.GetIndirectRelations
            or OpKind.AttributeExpr => true,
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
            case OpKind.AttributeExpr:
                batchOps.AddGetAttributesSingleEntityToBatch(batch, op.EntityType, op.EntityId,
                    PhysicalOpRunner.AttributeExprArgNames(op), ctx.SnapToken);
                break;
            case OpKind.CheckOp:
                ((IBatchableCheckOp)op.Op!).AddToBatch(batch, batchOps, ctx, op.EntityType, op.EntityId);
                break;
            default:
                throw new InvalidOperationException($"Unexpected batchable op kind {op.Kind}");
        }
    }

    private async Task ReadResult(DbDataReader reader, PendingOp op, CheckRequestContext ctx, IOpCompletionSink sink,
        CancellationToken ct)
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
                // .Transfer() hands CompleteWithPayload (which takes `object`) the underlying
                // List<T>, a reference type, so the payload costs no per-op boxing allocation;
                // CheckPlanExecutor re-wraps it in a PooledList<T> on the consuming side
                // (see CheckPlanExecutor.OnOpCompleted).
                sink.CompleteWithPayload(op.Token, pooled.Transfer());
                break;
            }
            case OpKind.AttributeExpr:
            {
                // Same 4-column shape as the providers' GetAttributesSingleEntity; the expression
                // itself is evaluated in-process, exactly as the non-batched path does.
                using var attributes = PooledList<AttributeTuple>.Rent();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    attributes.Add(new AttributeTuple(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        JsonNode.Parse(reader.GetString(3)) as JsonValue
                        ?? throw new InvalidOperationException("Attribute value must deserialize to a JsonValue.")));
                }
                sink.Complete(op.Token, PhysicalOpRunner.EvaluateAttributeExpr(schema, ctx, op, attributes.AsSpan()));
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
