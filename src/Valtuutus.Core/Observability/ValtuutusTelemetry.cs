using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Valtuutus.Core.Data;

namespace Valtuutus.Core.Observability;

/// <summary>
/// Spans on the <see cref="DefaultActivitySource.SourceName"/> source and instruments on
/// <see cref="ValtuutusMetrics.Meter"/> for engine calls, the database round trips they make,
/// writes, deletes and cache lookups. Nothing is allocated for either while nothing listens.
/// </summary>
public static class ValtuutusTelemetry
{
    internal const string EntityType = "valtuutus.entity_type";
    internal const string EntityId = "valtuutus.entity_id";
    internal const string Permission = "valtuutus.permission";
    internal const string SubjectType = "valtuutus.subject_type";
    internal const string SubjectId = "valtuutus.subject_id";
    internal const string SubjectRelation = "valtuutus.subject_relation";
    internal const string SnapTokenSource = "valtuutus.snap_token.source";
    internal const string SnapTokenValue = "valtuutus.snap_token";
    internal const string Result = "valtuutus.result";
    internal const string ResultCount = "valtuutus.result_count";
    internal const string RoundTrips = "valtuutus.round_trips";
    internal const string Steps = "valtuutus.steps";
    internal const string Step = "valtuutus.step";
    internal const string Queries = "valtuutus.queries";
    internal const string QueryCount = "valtuutus.query_count";
    internal const string ResolvesSnapToken = "valtuutus.resolves_snap_token";
    internal const string Straggler = "valtuutus.straggler";
    internal const string Operation = "valtuutus.operation";
    internal const string CacheHit = "valtuutus.cache.hit";
    internal const string TupleKind = "valtuutus.tuple.kind";
    internal const string DbSystemName = "db.system.name";
    internal const string ErrorType = "error.type";

    internal static readonly Histogram<double> OperationDuration = CreateDurationHistogram(
        "valtuutus.operation.duration",
        "Duration of engine calls as the caller waits for them, cache hits included.");

    internal static readonly Histogram<int> OperationRoundTrips =
        ValtuutusMetrics.Meter.CreateHistogram<int>("valtuutus.operation.round_trips", "{round_trip}",
            "Database round trips an engine call started before it answered.");

    internal static readonly Counter<long> CacheRequests =
        ValtuutusMetrics.Meter.CreateCounter<long>("valtuutus.cache.requests", "{request}",
            "Cached engine calls, by whether the cache answered.");

    internal static readonly Histogram<double> WriteDuration = CreateDurationHistogram(
        "valtuutus.write.duration",
        "Duration of writes and deletes; includes the commit when Valtuutus owns the transaction.");

    internal static readonly Counter<long> WriteTuples =
        ValtuutusMetrics.Meter.CreateCounter<long>("valtuutus.write.tuples", "{tuple}",
            "Relation and attribute tuples written.");

    private static readonly AsyncLocal<RequestTelemetry?> CurrentRequest = new();

    /// <summary>
    /// Whether anything listens to the span source. Callers check it before building data that only
    /// a span would carry.
    /// </summary>
    public static bool Enabled => DefaultActivitySource.Instance.HasListeners();

    private static bool OperationMetricsEnabled => OperationDuration.Enabled || OperationRoundTrips.Enabled;

    private static Histogram<double> CreateDurationHistogram(string name, string description) =>
#if NET9_0_OR_GREATER
        ValtuutusMetrics.Meter.CreateHistogram(name, "s", description, tags: null,
            new InstrumentAdvice<double>
            {
                // Engine calls take microseconds to tens of milliseconds. OpenTelemetry's default
                // boundaries (0, 5, 10, 25, ...) suit milliseconds; in seconds they put every call in
                // the first bucket.
                HistogramBucketBoundaries =
                    [0.0001, 0.00025, 0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5]
            });
#else
        ValtuutusMetrics.Meter.CreateHistogram<double>(name, "s", description);
#endif

    /// <summary>
    /// Starts the telemetry of an engine call: its span, and its metrics unless an outer cache call
    /// already measures it. It becomes the request every round trip started under it reports to.
    /// Synchronous on purpose: the request stays current for the rest of the calling async method and
    /// everything it starts, including work still running after it returns.
    /// </summary>
    internal static RequestTelemetry? StartRequest(TelemetryOperation operation, SnapToken? requestedSnapToken)
    {
        // An outer cache call only exists while something listens, so with nothing listening there
        // is no parent to report to either.
        if (!Enabled && !OperationMetricsEnabled) return null;
        var parent = CurrentRequest.Value;
        var measure = parent is null && OperationMetricsEnabled;
        var activity = Enabled
            ? DefaultActivitySource.Instance.StartActivity(operation.SpanName, ActivityKind.Internal)
            : null;
        if (activity is null && !measure && parent is null) return null;

        var request = new RequestTelemetry(operation, activity, parent, requestedSnapToken, measure);
        CurrentRequest.Value = request;
        return request;
    }

    /// <summary>
    /// Starts the span of one database round trip that runs a single query, named after the calling
    /// provider method.
    /// </summary>
    /// <param name="dbSystem">OpenTelemetry <c>db.system.name</c> of the provider; null when it is
    /// not a database.</param>
    /// <param name="query">The query the round trip runs.</param>
    public static ValtuutusSpan StartRoundTrip(string? dbSystem, [CallerMemberName] string query = "")
    {
        if (!Enabled)
        {
            if (OperationRoundTrips.Enabled) CurrentRequest.Value?.CountRoundTrip();
            return default;
        }
        return StartRoundTrip(dbSystem, [query], resolvesSnapToken: false);
    }

    /// <summary>Starts the span of one database round trip that runs several queries in one batch.</summary>
    /// <param name="dbSystem">OpenTelemetry <c>db.system.name</c> of the provider.</param>
    /// <param name="queries">The kind of each query in the batch, in order.</param>
    /// <param name="resolvesSnapToken">Whether the batch also resolves the request's latest snap token.</param>
    public static ValtuutusSpan StartRoundTrip(string? dbSystem, string[] queries, bool resolvesSnapToken)
    {
        if (!Enabled)
        {
            if (OperationRoundTrips.Enabled) CurrentRequest.Value?.CountRoundTrip();
            return default;
        }
        var request = CurrentRequest.Value;
        request?.CountRoundTrip();
        var activity = DefaultActivitySource.Instance.StartActivity("valtuutus.round_trip", ActivityKind.Client);
        if (activity is null) return default;

        if (dbSystem is not null) activity.SetTag(DbSystemName, dbSystem);
        activity.SetTag(Queries, queries);
        activity.SetTag(QueryCount, queries.Length);
        if (resolvesSnapToken) activity.SetTag(ResolvesSnapToken, true);
        if (request is { Step: > 0 and var step }) activity.SetTag(Step, step);
        return ValtuutusSpan.RoundTrip(activity, request);
    }

    /// <summary>Starts the telemetry of a write or delete, named after the calling provider method.</summary>
    /// <param name="dbSystem">OpenTelemetry <c>db.system.name</c> of the provider; null when it is
    /// not a database.</param>
    /// <param name="operation"><c>Write</c> or <c>Delete</c>.</param>
    public static ValtuutusSpan StartWrite(string? dbSystem, [CallerMemberName] string operation = "")
    {
        var name = operation == "Delete" ? "delete" : "write";
        var measure = WriteDuration.Enabled || WriteTuples.Enabled;
        var activity = Enabled
            ? DefaultActivitySource.Instance.StartActivity(
                name == "delete" ? "valtuutus.delete" : "valtuutus.write", ActivityKind.Client)
            : null;
        if (activity is null && !measure) return default;
        if (dbSystem is not null) activity?.SetTag(DbSystemName, dbSystem);
        return ValtuutusSpan.Write(activity, name, measure);
    }

    /// <summary>
    /// Starts the telemetry of a cached engine call. It measures the call for the operation metrics,
    /// so the engine call it falls through to on a miss does not measure it again.
    /// </summary>
    /// <param name="operation">The engine call: <c>check</c>, <c>subject_permission</c>,
    /// <c>lookup_entity</c> or <c>lookup_subject</c>.</param>
    /// <param name="requestedSnapToken">The snap token the caller passed, if any.</param>
    public static ValtuutusSpan StartCache(string operation, SnapToken? requestedSnapToken)
    {
        var measure = OperationMetricsEnabled || CacheRequests.Enabled;
        var activity = Enabled ? DefaultActivitySource.Instance.StartActivity("valtuutus.cache", ActivityKind.Internal) : null;
        if (activity is null && !measure) return default;

        activity?.SetTag(Operation, operation);
        var request = new RequestTelemetry(TelemetryOperation.ForCache(operation), activity, parent: null,
            requestedSnapToken, measure);
        CurrentRequest.Value = request;
        return ValtuutusSpan.Cache(request);
    }

    internal static bool RecordFailure(Activity? activity, Exception e)
    {
        if (activity is null) return false;
        activity.SetStatus(ActivityStatusCode.Error, e.Message);
        activity.SetTag(ErrorType, e.GetType().FullName);
        return false;
    }
}

/// <summary>An engine call, by the operation name metrics carry and the name of its span.</summary>
internal sealed class TelemetryOperation(string name, string spanName)
{
    public static readonly TelemetryOperation Check = new("check", "valtuutus.check");
    public static readonly TelemetryOperation SubjectPermission = new("subject_permission", "valtuutus.subject_permission");
    public static readonly TelemetryOperation Explain = new("explain", "valtuutus.explain");
    public static readonly TelemetryOperation LookupEntity = new("lookup_entity", "valtuutus.lookup_entity");
    public static readonly TelemetryOperation LookupSubject = new("lookup_subject", "valtuutus.lookup_subject");

    public string Name { get; } = name;
    public string SpanName { get; } = spanName;

    public static TelemetryOperation ForCache(string operation) => operation switch
    {
        "check" => Check,
        "subject_permission" => SubjectPermission,
        "lookup_entity" => LookupEntity,
        "lookup_subject" => LookupSubject,
        _ => new TelemetryOperation(operation, "valtuutus.cache"),
    };
}

/// <summary>
/// A round trip, write, delete or cached call; disposing it ends its span and records its metrics.
/// The default value stands for one that nothing listens to.
/// </summary>
public readonly struct ValtuutusSpan : IDisposable
{
    private enum SpanKind : byte { None, RoundTrip, Write, Cache }

    private readonly SpanKind _kind;
    private readonly Activity? _activity;
    private readonly RequestTelemetry? _request;
    private readonly WriteTelemetry? _write;

    private ValtuutusSpan(SpanKind kind, Activity? activity, RequestTelemetry? request, WriteTelemetry? write)
    {
        _kind = kind;
        _activity = activity;
        _request = request;
        _write = write;
    }

    internal static ValtuutusSpan RoundTrip(Activity activity, RequestTelemetry? request) =>
        new(SpanKind.RoundTrip, activity, request, null);

    internal static ValtuutusSpan Write(Activity? activity, string operation, bool measure) =>
        new(SpanKind.Write, activity, null, measure ? new WriteTelemetry(operation) : null);

    internal static ValtuutusSpan Cache(RequestTelemetry request) => new(SpanKind.Cache, null, request, null);

    /// <summary>Records the snap token the write or delete committed at.</summary>
    public void SetSnapToken(SnapToken snapToken) => _activity?.SetTag(ValtuutusTelemetry.SnapTokenValue, snapToken.Value);

    /// <summary>Records whether the cache answered the call.</summary>
    public void SetCacheHit(bool hit) => _request?.SetCacheHit(hit);

    /// <summary>Records the answer of a cached check.</summary>
    public void SetAllowed(bool allowed) => _request?.SetAllowed(allowed);

    /// <summary>
    /// Wraps the relation tuples of a write so they are counted as the provider reads them. Returns
    /// them unchanged when nothing measures writes.
    /// </summary>
    public IEnumerable<RelationTuple> CountRelations(IEnumerable<RelationTuple> relations) =>
        _write is null ? relations : _write.CountRelations(relations);

    /// <summary>
    /// Wraps the attribute tuples of a write so they are counted as the provider reads them. Returns
    /// them unchanged when nothing measures writes.
    /// </summary>
    public IEnumerable<AttributeTuple> CountAttributes(IEnumerable<AttributeTuple> attributes) =>
        _write is null ? attributes : _write.CountAttributes(attributes);

    /// <summary>
    /// Marks the span failed. Returns false, so it can sit in an exception filter that records the
    /// failure without catching it.
    /// </summary>
    public bool RecordFailure(Exception e)
    {
        _write?.Fail(e);
        if (_request is not null) return _request.Fail(e);
        return ValtuutusTelemetry.RecordFailure(_activity, e);
    }

    /// <summary>
    /// Marks the moment the database responded, for a round trip whose results are handed to the
    /// engine while they are still being read: the engine can answer from the first of them before
    /// the span ends, which does not make this round trip a straggler.
    /// </summary>
    public void Responded()
    {
        if (_kind != SpanKind.RoundTrip) return;
        _activity!.SetTag(ValtuutusTelemetry.Straggler, _request is { Answered: true });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        switch (_kind)
        {
            case SpanKind.RoundTrip:
                switch (_activity!.GetTagItem(ValtuutusTelemetry.Straggler))
                {
                    // The engine already returned its answer: nobody reads what this round trip brings back.
                    case null when _request is { Answered: true }:
                        _activity.SetTag(ValtuutusTelemetry.Straggler, true);
                        break;
                    // Decided when the database responded; the tag is only kept when true.
                    case false:
                        _activity.SetTag(ValtuutusTelemetry.Straggler, null);
                        break;
                }
                _activity.Dispose();
                break;
            case SpanKind.Write:
                _activity?.Dispose();
                _write?.Record();
                break;
            case SpanKind.Cache:
                _request!.Dispose();
                break;
        }
    }
}

/// <summary>Duration and tuple counts of one write or delete.</summary>
internal sealed class WriteTelemetry(string operation)
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _relations;
    private long _attributes;
    private string? _errorType;

    public IEnumerable<RelationTuple> CountRelations(IEnumerable<RelationTuple> relations)
    {
        foreach (var relation in relations)
        {
            _relations++;
            yield return relation;
        }
    }

    public IEnumerable<AttributeTuple> CountAttributes(IEnumerable<AttributeTuple> attributes)
    {
        foreach (var attribute in attributes)
        {
            _attributes++;
            yield return attribute;
        }
    }

    public void Fail(Exception e) => _errorType = e.GetType().FullName;

    public void Record()
    {
        var tags = new TagList { { ValtuutusTelemetry.Operation, operation } };
        if (_errorType is not null) tags.Add(ValtuutusTelemetry.ErrorType, _errorType);
        ValtuutusTelemetry.WriteDuration.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds, tags);
        if (_errorType is not null) return;
        if (_relations > 0)
            ValtuutusTelemetry.WriteTuples.Add(_relations, new KeyValuePair<string, object?>(ValtuutusTelemetry.TupleKind, "relation"));
        if (_attributes > 0)
            ValtuutusTelemetry.WriteTuples.Add(_attributes, new KeyValuePair<string, object?>(ValtuutusTelemetry.TupleKind, "attribute"));
    }
}

/// <summary>
/// One engine call or cached call: its span, the round trips started under it, and, for the
/// outermost call of a request, its metrics.
/// </summary>
internal sealed class RequestTelemetry : IDisposable
{
    private readonly TelemetryOperation _operation;
    private readonly Activity? _activity;
    private readonly RequestTelemetry? _parent;
    private readonly string _snapTokenSource;
    private readonly long _start;
    private int _roundTrips;
    private volatile bool _answered;
    private bool? _allowed;
    private bool? _cacheHit;
    private string? _errorType;

    public RequestTelemetry(TelemetryOperation operation, Activity? activity, RequestTelemetry? parent,
        SnapToken? requestedSnapToken, bool measure)
    {
        _operation = operation;
        _activity = activity;
        _parent = parent;
        _snapTokenSource = requestedSnapToken is null ? "latest" : "request";
        _start = measure ? Stopwatch.GetTimestamp() : 0;
        activity?.SetTag(ValtuutusTelemetry.SnapTokenSource, _snapTokenSource);
    }

    public int Step { get; private set; }

    public bool Answered => _answered;

    public void CountRoundTrip()
    {
        Interlocked.Increment(ref _roundTrips);
        _parent?.CountRoundTrip();
    }

    public void NextStep() => Step++;

    public void SetTag(string name, object? value) => _activity?.SetTag(name, value);

    public void SetAllowed(bool allowed)
    {
        _allowed = allowed;
        _activity?.SetTag(ValtuutusTelemetry.Result, allowed);
    }

    public void SetCacheHit(bool hit)
    {
        _cacheHit = hit;
        _activity?.SetTag(ValtuutusTelemetry.CacheHit, hit);
    }

    /// <summary>Records what every engine call reports once it has its answer.</summary>
    public void Answer(SnapToken? snapToken)
    {
        _answered = true;
        if (_activity is null) return;
        if (snapToken is { } token) _activity.SetTag(ValtuutusTelemetry.SnapTokenValue, token.Value);
        _activity.SetTag(ValtuutusTelemetry.RoundTrips, Volatile.Read(ref _roundTrips));
        if (Step > 0) _activity.SetTag(ValtuutusTelemetry.Steps, Step);
    }

    /// <summary>Marks the call failed; false, for use in an exception filter.</summary>
    public bool Fail(Exception e)
    {
        _errorType = e.GetType().FullName;
        return ValtuutusTelemetry.RecordFailure(_activity, e);
    }

    public void Dispose()
    {
        _answered = true;
        _activity?.Dispose();
        if (_start != 0) Record();
    }

    private void Record()
    {
        var tags = new TagList
        {
            { ValtuutusTelemetry.Operation, _operation.Name },
            { ValtuutusTelemetry.SnapTokenSource, _snapTokenSource },
        };
        if (_cacheHit is { } hit) tags.Add(ValtuutusTelemetry.CacheHit, hit);
        if (_errorType is not null) tags.Add(ValtuutusTelemetry.ErrorType, _errorType);

        if (_cacheHit is not null && _errorType is null)
            ValtuutusTelemetry.CacheRequests.Add(1, tags);

        if (_allowed is { } allowed) tags.Add(ValtuutusTelemetry.Result, allowed);
        ValtuutusTelemetry.OperationDuration.Record(Stopwatch.GetElapsedTime(_start).TotalSeconds, tags);
        ValtuutusTelemetry.OperationRoundTrips.Record(Volatile.Read(ref _roundTrips), tags);
    }
}
