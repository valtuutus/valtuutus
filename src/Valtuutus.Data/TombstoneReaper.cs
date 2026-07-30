using Valtuutus.Core.Data;

namespace Valtuutus.Data;

public interface ITombstoneReaperProvider
{
    /// <summary>
    /// Tries to atomically claim exclusivity for one bounded batch, then — only if claimed —
    /// deletes up to batchSize tombstoned rows per table (relation_tuples, attributes,
    /// transactions) with deleted_tx_id (or, for transactions, id) older than watermark, within
    /// the same connection/session the claim was made on. LockAcquired is false if another
    /// instance already holds the coordination lock for this batch — no rows are touched then.
    /// </summary>
    Task<ReapBatchResult> ReapBatchAsync(SnapToken watermark, int batchSize, CancellationToken ct);
}

public sealed record ReapBatchResult(
    int RelationTuplesDeleted,
    int AttributesDeleted,
    int TransactionsDeleted,
    bool MoreRemaining,
    bool LockAcquired);

public sealed record ReapResult(
    int RelationTuplesDeleted,
    int AttributesDeleted,
    int TransactionsDeleted,
    int BatchesRun,
    SnapToken? Watermark);

public interface ITombstoneReaper
{
    /// <summary>
    /// Runs a full sweep now: computes the watermark from configured retention, loops
    /// ReapBatchAsync (with the configured inter-batch pause) until no table has more eligible
    /// rows. Safe to call directly from any trigger.
    /// </summary>
    Task<ReapResult> ReapAsync(CancellationToken ct = default);
}

public sealed class TombstoneReaper(ITombstoneReaperProvider provider, ValtuutusReaperOptions options)
    : ITombstoneReaper
{
    public async Task<ReapResult> ReapAsync(CancellationToken ct = default)
    {
        var watermark = SnapTokenWatermark.Compute(DateTimeOffset.UtcNow, options.RetentionPeriod);
        int totalRelations = 0, totalAttributes = 0, totalTransactions = 0, batches = 0;
        bool more = true;

        while (more)
        {
            var batch = await provider.ReapBatchAsync(watermark, options.BatchSize, ct);
            batches++;

            if (batch.LockAcquired)
            {
                totalRelations += batch.RelationTuplesDeleted;
                totalAttributes += batch.AttributesDeleted;
                totalTransactions += batch.TransactionsDeleted;

                if (batch.RelationTuplesDeleted > 0)
                    ValtuutusDataMetrics.ReapedRows.Add(batch.RelationTuplesDeleted, new KeyValuePair<string, object?>("table", "relation_tuples"));
                if (batch.AttributesDeleted > 0)
                    ValtuutusDataMetrics.ReapedRows.Add(batch.AttributesDeleted, new KeyValuePair<string, object?>("table", "attributes"));
                if (batch.TransactionsDeleted > 0)
                    ValtuutusDataMetrics.ReapedRows.Add(batch.TransactionsDeleted, new KeyValuePair<string, object?>("table", "transactions"));
            }

            // Stop if this table is drained, or another instance is currently working this batch —
            // in the latter case, that other instance is presumably making progress on the backlog.
            more = batch.MoreRemaining && batch.LockAcquired;
            if (more)
                await Task.Delay(options.PauseBetweenBatches, ct);
        }

        return new ReapResult(totalRelations, totalAttributes, totalTransactions, batches, watermark);
    }
}
