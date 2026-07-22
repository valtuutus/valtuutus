using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

/// <summary>Discriminates what a <see cref="PendingOp"/> asks the physical executor to do.</summary>
public enum OpKind : byte
{
    /// <summary>Direct relation lookup; <see cref="PendingOp.Relation"/> holds the relation name.</summary>
    HasDirectRelation,
    /// <summary>Boolean attribute truth test; <see cref="PendingOp.Relation"/> holds the attribute name.</summary>
    HasTrueBoolAttribute,
    /// <summary>Attribute expression evaluation; <see cref="PendingOp.Expr"/> holds the expression.</summary>
    AttributeExpr,
    /// <summary>Tuple-to-userset fast path; <see cref="PendingOp.Relation"/> holds the tupleset relation,
    /// with <see cref="PendingOp.SubEntityType"/> and <see cref="PendingOp.ComputedRelation"/> populated.</summary>
    TtuFastPath,
    /// <summary>Any-of direct relation over a batch of entity ids; <see cref="PendingOp.EntityType"/> holds the
    /// sub-entity type, <see cref="PendingOp.EntityIds"/> the candidate ids, and
    /// <see cref="PendingOp.Relation"/> the computed relation.</summary>
    HasAnyDirectRelation,
    /// <summary>Set-shaped relation fetch; payload is a <see cref="Pools.PooledList{T}"/> of
    /// <see cref="RelationTuple"/>; <see cref="PendingOp.Relation"/> holds the
    /// tupleset/relation name.</summary>
    GetRelations,
    /// <summary>Set-shaped indirect relation fetch; payload is a <see cref="Pools.PooledList{T}"/>
    /// of <see cref="RelationTuple"/>.</summary>
    GetIndirectRelations,
    /// <summary>Rewriter-installed <see cref="ICheckOp"/> carried in <see cref="PendingOp.Op"/>;
    /// the executor is opaque to its contents.</summary>
    CheckOp,
}

internal static class OpKindMeta
{
    // Self-adjusting for the common case (appending a member after CheckOp) instead of a
    // hand-maintained magic number — sizes a stackalloc same-kind tally in
    // CheckPlanExecutor.FlushWave (M3 telemetry) so it never heap-allocates on the hot path.
    internal const int OpKindCount = (int)OpKind.CheckOp + 1;
}

/// <summary>One unit of physical work submitted to an <see cref="IPhysicalExecutor"/>.</summary>
/// <remarks>
/// Which fields beyond <see cref="Token"/>/<see cref="Kind"/>/<see cref="EntityType"/>/<see cref="EntityId"/>
/// are populated is keyed by <see cref="Kind"/> — see the per-member docs on <see cref="OpKind"/>.
/// Evolution contract: new <see cref="OpKind"/> members and new nullable fields are additive and
/// non-breaking; executors must treat unrecognized kinds as unhandled and either delegate them to
/// <see cref="PhysicalOpRunner"/> or fail the token.
/// </remarks>
public readonly struct PendingOp
{
    /// <summary>Request-scoped correlation token; every completion reported to the sink names it.</summary>
    public required int Token { get; init; }
    /// <summary>What to do — keys which of the optional fields below are populated.</summary>
    public required OpKind Kind { get; init; }
    /// <summary>Entity type the op targets.</summary>
    public required string EntityType { get; init; }
    /// <summary>Entity id the op targets (empty for <see cref="OpKind.HasAnyDirectRelation"/>,
    /// which targets <see cref="EntityIds"/> instead).</summary>
    public required string EntityId { get; init; }
    /// <summary>Relation, attribute, or tupleset name, per <see cref="Kind"/>.</summary>
    public string? Relation { get; init; }
    /// <summary>Candidate entity ids for <see cref="OpKind.HasAnyDirectRelation"/>.</summary>
    public string[]? EntityIds { get; init; }
    /// <summary>Sub-entity type for <see cref="OpKind.TtuFastPath"/>.</summary>
    public string? SubEntityType { get; init; }
    /// <summary>Computed relation for <see cref="OpKind.TtuFastPath"/>.</summary>
    public string? ComputedRelation { get; init; }
    /// <summary>Expression for <see cref="OpKind.AttributeExpr"/>.</summary>
    public PermissionNodeLeafExp? Expr { get; init; }
    /// <summary>Rewriter-installed op for <see cref="OpKind.CheckOp"/>.</summary>
    public ICheckOp? Op { get; init; }
}

/// <summary>Where an <see cref="IPhysicalExecutor"/> reports each op's outcome.</summary>
/// <remarks>
/// Implementations are thread-safe: providers may complete tokens concurrently from any thread,
/// including synchronously inside <see cref="IPhysicalExecutor.Submit"/>. Every submitted token must
/// receive exactly one call to exactly one of the three members.
/// </remarks>
public interface IOpCompletionSink
{
    /// <summary>Reports a boolean result for the op identified by <paramref name="token"/>.</summary>
    void Complete(int token, bool result);
    /// <summary>Reports a set-shaped result for the op identified by <paramref name="token"/>; the payload
    /// type is keyed by the op's <see cref="OpKind"/> (a <see cref="Pools.PooledList{T}"/> of
    /// <see cref="RelationTuple"/> for
    /// <see cref="OpKind.GetRelations"/>/<see cref="OpKind.GetIndirectRelations"/>). Ownership of a
    /// disposable payload transfers to the sink.</summary>
    void CompleteWithPayload(int token, object payload);
    /// <summary>Reports a terminal failure for the op identified by <paramref name="token"/> — per-token:
    /// it counts as that token's one completion, and failing one token does not discharge its
    /// siblings — each still needs its own completion.</summary>
    void Fail(int token, Exception error);
}

/// <summary>
/// Executes the physical ops of a check request. Implementors receive waves — groups of ops that
/// become ready together — of <see cref="PendingOp"/>s and report every outcome to the
/// <see cref="IOpCompletionSink"/>.
/// </summary>
/// <remarks>
/// Below, "the engine" is the calling plan driver that invokes <see cref="Submit"/>; "the
/// implementation" is your executor.
/// <para>Runtime execution contract — the semantics an implementation must uphold:</para>
/// <list type="number">
/// <item><description><b>Exactly-once, always.</b> Every submitted token receives exactly one
/// <see cref="IOpCompletionSink.Complete"/>/<see cref="IOpCompletionSink.CompleteWithPayload"/>/<see cref="IOpCompletionSink.Fail"/>,
/// even under cancellation or provider fault. State behind the tokens is pooled; the engine
/// releases it only when the outstanding count reaches zero, so dropping a token leaks that state
/// and stalls the request forever. A provider that merged N ops into one query that failed
/// fails all N tokens with the same exception.</description></item>
/// <item><description><b>Failure is per-token, terminal, and fail-fast.</b> The first
/// <see cref="IOpCompletionSink.Fail"/> faults the whole check request — authorization must not
/// guess around missing data. The engine stops submitting further waves, throws the error to the
/// caller, and retires the faulted request state (late sibling completions are absorbed, never
/// misrouted into another request).</description></item>
/// <item><description><b>Cancellation is deliberately asymmetric.</b> The request-level
/// <see cref="CancellationToken"/> passed to <see cref="Submit"/> is observed, but every token must
/// still complete — <see cref="IOpCompletionSink.Fail"/> with an
/// <see cref="OperationCanceledException"/> is acceptable. There is NO logical-cancel API for
/// in-flight ops: the engine drops stale completions itself. Its absence is a decision, not an
/// omission.</description></item>
/// <item><description><b>Retries are provider-owned and invisible.</b> All check ops are snapshot
/// reads, idempotent by construction, so an implementation may retry internally as it sees fit; the
/// engine treats <see cref="IOpCompletionSink.Fail"/> as terminal.</description></item>
/// <item><description><b>No ordering guarantees.</b> Completions may arrive in any order, on any
/// thread; synchronous completion inside <see cref="Submit"/> is permitted. The sink is thread-safe
/// (providers complete concurrently); the engine is the single consumer.</description></item>
/// <item><description><b>Incremental by construction.</b> Report each result when you have it — the
/// engine short-circuits mid-wave and drops leftovers as stale. An implementation that merges ops
/// trades incrementality for fewer round trips; that choice and its cost model belong to the
/// implementation.</description></item>
/// <item><description><b>Overlapping waves are allowed.</b> <see cref="Submit"/> is called
/// repeatedly per request as new work becomes ready; the token space is request-scoped and there
/// are no wave barriers — ops from different waves may be in flight simultaneously.</description></item>
/// </list>
/// </remarks>
public interface IPhysicalExecutor
{
    /// <summary>Data source for the current request. Set fresh before every request (the underlying
    /// reader is DI-scoped; executor instances are pooled and outlive any one request) — never
    /// constructor-captured.</summary>
    IDataReaderProvider Reader { set; }

    /// <summary>Submits one wave of ready ops. Every op's outcome must eventually be reported to
    /// <paramref name="sink"/> under the contract in the type-level remarks.</summary>
    void Submit(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct);
}

/// <summary>The reference <see cref="IPhysicalExecutor"/>: runs each submitted op as its own
/// independent operation via <see cref="PhysicalOpRunner"/>, with no batching.</summary>
// Poolable via CheckPlanExecutorPool: schema is a DI singleton so it's fixed at construction,
// but Reader wraps the per-request scoped IDataReaderProvider and must be set fresh on every
// rent (see CheckPlanExecutor's identical Physical field for the same reasoning).
public sealed class DefaultPhysicalExecutor(Schema schema) : IPhysicalExecutor
{
    /// <summary>See <see cref="IPhysicalExecutor.Reader"/>. Adds a getter on top of the
    /// interface's set-only member so callers can read the current reader back.</summary>
    public IDataReaderProvider Reader { get; set; } = null!;

    /// <inheritdoc />
    public void Submit(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
    {
        foreach (var op in ops)
            _ = PhysicalOpRunner.RunAsync(op, Reader, schema, ctx, sink, ct);
    }
}

/// <summary>The shared per-op execution logic. Custom <see cref="IPhysicalExecutor"/>
/// implementations (e.g. batching executors) reuse this for whatever a wave can't batch — wrong
/// provider, unbatchable op kind — instead of duplicating the <see cref="OpKind"/> switch.</summary>
public static class PhysicalOpRunner
{
    /// <summary>Runs a single op against <paramref name="reader"/> and reports its outcome to
    /// <paramref name="sink"/> — exactly one completion per call, exceptions routed to
    /// <see cref="IOpCompletionSink.Fail"/>.</summary>
    public static async Task RunAsync(PendingOp op, IDataReaderProvider reader, Schema schema,
        CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
    {
        try
        {
            switch (op.Kind)
            {
                case OpKind.HasDirectRelation:
                    sink.Complete(op.Token, await reader.HasDirectRelation(
                        Filter(op, op.Relation!, ctx), ctx.SubjectId!, ct).ConfigureAwait(false));
                    break;
                case OpKind.HasTrueBoolAttribute:
                    sink.Complete(op.Token, await reader.HasTrueBoolAttribute(
                        op.EntityType, op.EntityId, op.Relation!, ctx.SnapToken, ct).ConfigureAwait(false));
                    break;
                case OpKind.AttributeExpr:
                    sink.Complete(op.Token, await AttributeExpressionEvaluator.Evaluate(
                        reader, schema, ctx, op.EntityType, op.EntityId, op.Expr!, ct).ConfigureAwait(false));
                    break;
                case OpKind.TtuFastPath:
                    sink.Complete(op.Token, await reader.HasTupleToUserSetRelation(
                        op.EntityType, op.EntityId, op.Relation!, op.SubEntityType!, op.ComputedRelation!,
                        ctx.SubjectType!, ctx.SubjectId!, ctx.SnapToken, ct).ConfigureAwait(false));
                    break;
                case OpKind.HasAnyDirectRelation:
                    sink.Complete(op.Token, await reader.HasAnyDirectRelation(
                        op.EntityType, op.EntityIds!, op.Relation!, ctx.SubjectId!, ctx.SnapToken, ct).ConfigureAwait(false));
                    break;
                case OpKind.GetRelations:
                    sink.CompleteWithPayload(op.Token, await reader.GetRelations(
                        Filter(op, op.Relation!, ctx), ct).ConfigureAwait(false));
                    break;
                case OpKind.GetIndirectRelations:
                    sink.CompleteWithPayload(op.Token, await reader.GetIndirectRelations(
                        Filter(op, op.Relation!, ctx), ct).ConfigureAwait(false));
                    break;
                case OpKind.CheckOp:
                    sink.Complete(op.Token, await op.Op!.Execute(reader, ctx, op.EntityType, op.EntityId, ct)
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
