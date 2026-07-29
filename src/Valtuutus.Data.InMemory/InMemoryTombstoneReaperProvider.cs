using Valtuutus.Core.Data;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class InMemoryTombstoneReaperProvider(RelationsStore relations, AttributesStore attributes)
    : ITombstoneReaperProvider
{
    public Task<ReapBatchResult> ReapBatchAsync(SnapToken watermark, int batchSize, CancellationToken ct)
    {
        // Single process — nothing to coordinate against, so LockAcquired is unconditionally true.
        var watermarkUlid = Ulid.Parse(watermark.Value);
        var relationsDeleted = relations.ReapTombstones(watermarkUlid);
        var attributesDeleted = attributes.ReapTombstones(watermarkUlid);

        // In-memory reap isn't chunked by batchSize (no I/O cost to bound) — always fully drained
        // in one call, so there is never "more remaining" from this provider's perspective.
        return Task.FromResult(new ReapBatchResult(relationsDeleted, attributesDeleted, TransactionsDeleted: 0, MoreRemaining: false, LockAcquired: true));
    }
}
