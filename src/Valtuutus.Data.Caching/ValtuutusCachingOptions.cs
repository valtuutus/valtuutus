namespace Valtuutus.Data.Caching;

public sealed record ValtuutusCachingOptions
{
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
}
