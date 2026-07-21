using System.Data.Common;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.Db;

/// <summary>
/// Opt-in capability for a rewriter-installed <see cref="ICheckOp"/>: participate in a shared
/// <see cref="DbBatch"/> instead of running as its own individual round trip.
/// </summary>
/// <remarks>
/// The batching executor discovers this capability by type test — an <see cref="ICheckOp"/> that
/// doesn't implement it simply falls back to running individually via
/// <see cref="PhysicalOpRunner"/>; there are no hardcoded checks against concrete op classes. This
/// is the extension point for third-party fused ops.
/// </remarks>
public interface IBatchableCheckOp
{
    /// <summary>Adds this op's command to the batch — via the schema/table-aware
    /// <see cref="IRelationalBatchOps"/> methods, never by re-templating SQL inline here.</summary>
    /// <remarks>
    /// Contract: adds EXACTLY ONE command per call. The batching executor correlates result-set
    /// order to op tokens via a positional 1:1 zip against the sequence AddToBatch calls were made
    /// in, so an implementation that added zero or more than one command would silently misroute
    /// every result after it.
    /// </remarks>
    void AddToBatch(DbBatch batch, IRelationalBatchOps ops, CheckRequestContext ctx,
        string entityType, string entityId);

    /// <summary>Reads this op's own result from <paramref name="reader"/> (already positioned at
    /// this op's result set by the batching executor) and reports it to <paramref name="sink"/>
    /// for <paramref name="token"/>. Must consume only its own result set.</summary>
    Task ReadResultAsync(DbDataReader reader, int token, IOpCompletionSink sink, CancellationToken ct);
}
