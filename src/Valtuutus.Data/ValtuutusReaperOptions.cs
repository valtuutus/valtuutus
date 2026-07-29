namespace Valtuutus.Data;

public sealed record ValtuutusReaperOptions
{
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public int BatchSize { get; set; } = 1000;
    public TimeSpan PauseBetweenBatches { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Only consumed by AddTombstoneReaperHostedService (Valtuutus.Data.BackgroundService) —
    /// ignored if you call ITombstoneReaper yourself from your own trigger.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);
}
