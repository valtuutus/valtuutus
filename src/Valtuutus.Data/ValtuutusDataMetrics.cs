using System.Diagnostics.Metrics;
using Valtuutus.Core.Observability;

namespace Valtuutus.Data;

/// <summary>
/// Data-layer metrics defined on the shared Core "Valtuutus" meter. Any assembly holding a
/// reference to ValtuutusMetrics.Meter can create new instruments on it — no Core changes needed.
/// </summary>
internal static class ValtuutusDataMetrics
{
    internal static readonly Counter<long> ReapedRows =
        ValtuutusMetrics.Meter.CreateCounter<long>("valtuutus.data.reaper.reaped_rows");
}
