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

    /// <summary>Expression nodes decided by the short-circuit value before all children finished.
    /// Emitted by both engines; under CheckEngineV2, counts Union/Intersect expression frames
    /// only (V1 also counts TTU fan-out short-circuits).</summary>
    internal static readonly Counter<long> ShortCircuits =
        Meter.CreateCounter<long>("valtuutus.check.short_circuits");

    /// <summary>Expression nodes where child index 0 alone would have decided the node
    /// (sequential-first scheduling would have saved the sibling queries).
    /// Emitted by both engines; under CheckEngineV2, counts Union/Intersect expression frames
    /// only (V1 also counts TTU fan-out short-circuits).</summary>
    internal static readonly Counter<long> FirstChildDecided =
        Meter.CreateCounter<long>("valtuutus.check.first_child_decided");

    /// <summary>Request-scoped memo hits inside CheckEngine.</summary>
    internal static readonly Counter<long> MemoHits =
        Meter.CreateCounter<long>("valtuutus.check.memo_hits");

    /// <summary>Ops submitted per DrainReady wave (one Physical.Submit call). V2 only — V1 has
    /// no wave concept. M3 telemetry: the ops-per-Submit half of the true batching-headroom
    /// signal (design doc "M3 — wave-shaped telemetry").</summary>
    internal static readonly Histogram<long> WaveOps =
        Meter.CreateHistogram<long>("valtuutus.check.wave_ops");

    /// <summary>Ops within a wave that share their OpKind with at least one sibling in the same
    /// wave — i.e., ops a same-shape coalescer could merge. V2 only. M3 telemetry: the
    /// same-OpKind-per-wave half of the batching-headroom signal; divide by the sum of
    /// <c>valtuutus.check.wave_ops</c> for the ratio the frontier-batching gate's decision rule
    /// wants (design doc "Reading the frontier-batching gate").</summary>
    internal static readonly Counter<long> WaveSameKindOps =
        Meter.CreateCounter<long>("valtuutus.check.wave_same_kind_ops");

    /// <summary>Provider-level DB/store queries issued (all read methods).</summary>
    public static readonly Counter<long> DbQueries =
        Meter.CreateCounter<long>("valtuutus.db.queries");
}
