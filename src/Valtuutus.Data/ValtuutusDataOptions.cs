namespace Valtuutus.Data;

/// <summary>
/// POCO that stores the options for the Valtuutus.Data library.
/// </summary>
public record ValtuutusDataOptions
{
    /// <summary>
    /// The amount of concurrent queries that a Check Request can evaluate at the same time.
    /// </summary>
    public int MaxConcurrentQueries { get; internal set; } = 5;
}