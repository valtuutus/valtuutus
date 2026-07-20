using System.Data.Common;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.Db;

// Opt-in capability for a rewriter-installed ICheckOp: participate in a shared DbBatch instead
// of running as its own individual round trip. HasAnyOfDirectRelationsOp and
// HasAnyOfAttributesOp both implement this; a genuinely third-party/unrecognized ICheckOp
// simply doesn't, and BatchedPhysicalExecutor (a later task) falls back to running it
// individually — no hardcoded type-checks against concrete op classes.
internal interface IBatchableCheckOp
{
    // Adds this op's command to the batch (via the schema/table-aware IRelationalBatchOps
    // method — never re-templates SQL inline here). Contract: adds EXACTLY ONE command per
    // call — BatchedPhysicalExecutor correlates result-set order to op tokens via a positional
    // 1:1 zip against the sequence AddToBatch calls were made in, so a future implementation
    // that added zero or more than one command would silently misroute every result after it.
    void AddToBatch(DbBatch batch, IRelationalBatchOps ops, CheckRequestContext ctx,
        string entityType, string entityId);

    // Reads this op's own result from the reader (positioned at its command's result set by the
    // caller — a later task's BatchedPhysicalExecutor) and reports it to the sink.
    Task ReadResultAsync(DbDataReader reader, int token, IOpCompletionSink sink, CancellationToken ct);
}
