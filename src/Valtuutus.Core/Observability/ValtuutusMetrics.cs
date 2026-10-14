using System.Diagnostics.Metrics;

namespace Valtuutus.Core.Observability;

/// <summary>
/// Opt-in workload-characterization metrics. Counters cost ~nothing unless a listener
/// subscribes (e.g. `dotnet-counters monitor --counters Valtuutus -p PID`).
/// All counters are process-wide totals; derive per-check averages by dividing by
/// <c>valtuutus.check.requests</c>.
/// </summary>
public static class ValtuutusMetrics
{
    public static readonly Meter Meter = new("Valtuutus", "1.0");

    /// <summary>Public Check/SubjectPermission entry calls.</summary>
    internal static readonly Counter<long> CheckRequests =
        Meter.CreateCounter<long>("valtuutus.check.requests");

    /// <summary>Union/Intersect nodes evaluated with 2+ live children.</summary>
    internal static readonly Counter<long> ExpressionNodes =
        Meter.CreateCounter<long>("valtuutus.check.expression_nodes");

    /// <summary>Union/Intersect expression nodes decided by the short-circuit value before all
    /// children finished.</summary>
    internal static readonly Counter<long> ShortCircuits =
        Meter.CreateCounter<long>("valtuutus.check.short_circuits");

    /// <summary>Union/Intersect expression nodes where child index 0 alone would have decided the
    /// node (sequential-first scheduling would have saved the sibling queries).</summary>
    internal static readonly Counter<long> FirstChildDecided =
        Meter.CreateCounter<long>("valtuutus.check.first_child_decided");

    /// <summary>Request-scoped memo hits inside the check engine.</summary>
    internal static readonly Counter<long> MemoHits =
        Meter.CreateCounter<long>("valtuutus.check.memo_hits");

    /// <summary>Ops submitted per DrainReady wave (one Physical.Submit call). The ops-per-Submit half of the batching-headroom signal: how many ops
    /// land in the same wave and could in principle be coalesced into fewer round trips.</summary>
    internal static readonly Histogram<long> WaveOps =
        Meter.CreateHistogram<long>("valtuutus.check.wave_ops");

    /// <summary>Ops within a wave that share their OpKind with at least one sibling in the same
    /// wave — i.e., ops a same-shape coalescer could merge. V2 only. The same-OpKind-per-wave
    /// half of the batching-headroom signal; divide by the sum of
    /// <c>valtuutus.check.wave_ops</c> for the fraction of a wave that's actually coalescable
    /// — the ratio a decision to invest in frontier batching should be judged against.</summary>
    internal static readonly Counter<long> WaveSameKindOps =
        Meter.CreateCounter<long>("valtuutus.check.wave_same_kind_ops");

    /// <summary>Provider-level DB/store queries issued (all read methods).</summary>
    public static readonly Counter<long> DbQueries =
        Meter.CreateCounter<long>("valtuutus.db.queries");
}
