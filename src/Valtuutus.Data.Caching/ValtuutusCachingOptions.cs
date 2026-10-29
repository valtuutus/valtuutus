using Valtuutus.Core.Data;

namespace Valtuutus.Data.Caching;

public sealed record ValtuutusCachingOptions
{
    private static readonly TimeSpan UnsettledResultDuration = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long after its transaction id was taken a snap token can still gain rows. Writers take
    /// their id before committing, so a write that commits late lands at an older token than one
    /// already committed. A result read at a token younger than this is cached for at most one
    /// second instead of <see cref="Duration"/>, which bounds how long it can be served after such a write.
    /// <see cref="TimeSpan.Zero"/> caches every result for <see cref="Duration"/>.
    /// </summary>
    public TimeSpan SettleWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a result read at a snap token is cached.
    /// </summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the latest snap token is kept. Requests without a token read at it, and only writes
    /// made through Valtuutus move it forward, so this bounds how long such requests can miss a
    /// write made to the tables some other way.
    /// </summary>
    public TimeSpan LatestSnapTokenDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The clock a snap token's age is measured with. <c>AddCaching</c> sets it to the
    /// <see cref="System.TimeProvider"/> registered in the service collection, when there is one.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// How long to cache a result read at <paramref name="snapToken"/>. The token's age comes from
    /// the timestamp in its ULID; a token that is not a ULID counts as settled.
    /// </summary>
    internal TimeSpan ResultDuration(SnapToken snapToken)
    {
        if (SettleWindow > TimeSpan.Zero
            && Ulid.TryParse(snapToken.Value, out var id)
            && TimeProvider.GetUtcNow() - id.Time < SettleWindow)
        {
            return UnsettledResultDuration < Duration ? UnsettledResultDuration : Duration;
        }

        return Duration;
    }
}
