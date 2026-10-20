using System.Diagnostics;
using Valtuutus.Core.Observability;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// Collects the spans emitted on the Valtuutus source under one test. Every test class shares the
/// process-wide source, so the collector opens its own root span and keeps only the spans of that
/// trace. Create it in the test method itself so the root stays current for the code under test.
/// </summary>
public sealed class SpanCollector : IDisposable
{
    private const string TestSourceName = "Valtuutus.Tests.Tracing";
    private static readonly ActivitySource TestSource = new(TestSourceName);

    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = [];
    private readonly Activity _root;

    public SpanCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is DefaultActivitySource.SourceName or TestSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_stopped) _stopped.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
        _root = TestSource.StartActivity("test")!;
    }

    /// <summary>The spans of this test's trace that have ended so far.</summary>
    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_stopped)
                return _stopped.Where(a => a.TraceId == _root.TraceId && !ReferenceEquals(a, _root)).ToList();
        }
    }

    public IReadOnlyList<Activity> Named(string name) => Spans.Where(a => a.OperationName == name).ToList();

    public IReadOnlyList<Activity> ChildrenOf(Activity parent) =>
        Spans.Where(a => a.ParentSpanId == parent.SpanId).ToList();

    public void Dispose()
    {
        _root.Dispose();
        _listener.Dispose();
    }
}
