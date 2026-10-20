using System.Diagnostics.Metrics;
using Valtuutus.Core.Observability;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// Collects the measurements recorded on the Valtuutus meter under one test. The meter is
/// process-wide and its tags carry no ids, so the collector keeps only the measurements recorded in
/// the async flow that created it. Create it in the test method itself.
/// </summary>
public sealed class MeasurementCollector : IDisposable
{
    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private static readonly AsyncLocal<MeasurementCollector?> Owner = new();

    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = [];

    public MeasurementCollector()
    {
        Owner.Value = this;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == ValtuutusMetrics.Meter) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
        _listener.SetMeasurementEventCallback<int>((i, v, t, _) => Add(i, v, t));
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
        _listener.Start();
    }

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (!ReferenceEquals(Owner.Value, this)) return;
        var copy = new Dictionary<string, object?>();
        foreach (var tag in tags) copy[tag.Key] = tag.Value;
        lock (_measurements) _measurements.Add(new Measurement(instrument.Name, value, copy));
    }

    public IReadOnlyList<Measurement> Of(string instrument)
    {
        lock (_measurements) return _measurements.Where(m => m.Instrument == instrument).ToList();
    }

    public void Dispose() => _listener.Dispose();
}
