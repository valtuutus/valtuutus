using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal enum OpKind : byte
{
    HasDirectRelation,      // Relation
    HasTrueBoolAttribute,   // Relation = attribute name
    AttributeExpr,          // Expr
    TtuFastPath,            // Relation = tupleset, SubEntityType, ComputedRelation
    HasAnyDirectRelation,   // EntityType = sub entity type, EntityIds, Relation = computed relation
    GetRelations,           // payload: PooledList<RelationTuple>; Relation = tupleset/relation name
    GetIndirectRelations,   // payload: PooledList<RelationTuple>
    HasAnyOfDirectRelations, // payload: HashSet<string>; Relations = sibling relation names
    CheckOp,                // Op = rewriter-installed ICheckOp; executor is opaque to its contents
}

internal static class OpKindMeta
{
    // Self-adjusting for the common case (appending a member after CheckOp) instead of a
    // hand-maintained magic number — sizes a stackalloc same-kind tally in
    // CheckPlanExecutor.FlushWave (M3 telemetry) so it never heap-allocates on the hot path.
    internal const int OpKindCount = (int)OpKind.CheckOp + 1;
}

internal readonly struct PendingOp
{
    public required int Token { get; init; }
    public required OpKind Kind { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public string? Relation { get; init; }
    public string[]? EntityIds { get; init; }
    public string? SubEntityType { get; init; }
    public string? ComputedRelation { get; init; }
    public PermissionNodeLeafExp? Expr { get; init; }
    public string[]? Relations { get; init; }
    public ICheckOp? Op { get; init; }
}

internal interface IOpCompletionSink
{
    void Complete(int token, bool result);
    void CompleteWithPayload(int token, object payload);
    void Fail(int token, Exception error);
}

internal interface IPhysicalExecutor
{
    // Contract (design doc "Runtime execution contract"): exactly-once completion per token,
    // any order, any thread, synchronous completion inside Submit permitted, failures per-token.
    void Submit(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct);
}

// Poolable via CheckPlanExecutorPool: schema is a DI singleton so it's fixed at construction,
// but Reader wraps the per-request scoped IDataReaderProvider and must be set fresh on every
// rent (see CheckPlanExecutor's identical Physical field for the same reasoning).
internal sealed class DefaultPhysicalExecutor(Schema schema) : IPhysicalExecutor
{
    internal IDataReaderProvider Reader = null!;

    public void Submit(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
    {
        foreach (var op in ops)
            _ = RunAsync(op, ctx, sink, ct);
    }

    private async Task RunAsync(PendingOp op, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
    {
        try
        {
            switch (op.Kind)
            {
                case OpKind.HasDirectRelation:
                    sink.Complete(op.Token, await Reader.HasDirectRelation(
                        Filter(op, op.Relation!, ctx), ctx.SubjectId!, ct).ConfigureAwait(false));
                    break;
                case OpKind.HasTrueBoolAttribute:
                    sink.Complete(op.Token, await Reader.HasTrueBoolAttribute(
                        op.EntityType, op.EntityId, op.Relation!, ctx.SnapToken, ct).ConfigureAwait(false));
                    break;
                case OpKind.AttributeExpr:
                    sink.Complete(op.Token, await AttributeExpressionEvaluator.Evaluate(
                        Reader, schema, ctx, op.EntityType, op.EntityId, op.Expr!, ct).ConfigureAwait(false));
                    break;
                case OpKind.TtuFastPath:
                    sink.Complete(op.Token, await Reader.HasTupleToUserSetRelation(
                        op.EntityType, op.EntityId, op.Relation!, op.SubEntityType!, op.ComputedRelation!,
                        ctx.SubjectType!, ctx.SubjectId!, ctx.SnapToken, ct).ConfigureAwait(false));
                    break;
                case OpKind.HasAnyDirectRelation:
                    sink.Complete(op.Token, await Reader.HasAnyDirectRelation(
                        op.EntityType, op.EntityIds!, op.Relation!, ctx.SubjectId!, ctx.SnapToken, ct).ConfigureAwait(false));
                    break;
                case OpKind.GetRelations:
                    sink.CompleteWithPayload(op.Token, await Reader.GetRelations(
                        Filter(op, op.Relation!, ctx), ct).ConfigureAwait(false));
                    break;
                case OpKind.GetIndirectRelations:
                    sink.CompleteWithPayload(op.Token, await Reader.GetIndirectRelations(
                        Filter(op, op.Relation!, ctx), ct).ConfigureAwait(false));
                    break;
                case OpKind.HasAnyOfDirectRelations:
                    sink.CompleteWithPayload(op.Token, await Reader.HasAnyOfDirectRelations(
                        op.EntityType, op.EntityId, op.Relations!, ctx.SubjectId!, ctx.SnapToken, ct).ConfigureAwait(false));
                    break;
                case OpKind.CheckOp:
                    sink.Complete(op.Token, await op.Op!.Execute(Reader, ctx, op.EntityType, op.EntityId, ct)
                        .ConfigureAwait(false));
                    break;
                default:
                    sink.Fail(op.Token, new InvalidOperationException($"Unknown op kind {op.Kind}"));
                    break;
            }
        }
        catch (Exception e)
        {
            sink.Fail(op.Token, e);
        }
    }

    private static RelationTupleFilter Filter(in PendingOp op, string relation, CheckRequestContext ctx) => new()
    {
        EntityType = op.EntityType,
        EntityId = op.EntityId,
        Relation = relation,
        SnapToken = ctx.SnapToken
    };
}
