using Valtuutus.Core.Data;

namespace Valtuutus.Data;

/// <summary>
/// Computes the time-based watermark used by the tombstone reaper: a ULID for
/// (<paramref name="now"/> - <paramref name="retention"/>) with zeroed randomness, the smallest
/// possible ULID at that timestamp. A stored deleted_tx_id/created_tx_id string-compares below
/// this watermark if and only if it is strictly older than the retention cutoff.
/// </summary>
public static class SnapTokenWatermark
{
    public static SnapToken Compute(DateTimeOffset now, TimeSpan retention)
    {
        var cutoff = now - retention;
        Span<byte> bytes = stackalloc byte[16]; // 6-byte big-endian timestamp + 10 zero bytes (randomness)
        long ms = cutoff.ToUnixTimeMilliseconds();
        for (int i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }
        var watermark = new Ulid(bytes);
        return new SnapToken(watermark.ToString());
    }
}
