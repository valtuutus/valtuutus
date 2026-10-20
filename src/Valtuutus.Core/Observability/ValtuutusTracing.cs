using System.Diagnostics;
using System.Runtime.CompilerServices;
using Valtuutus.Core.Data;

namespace Valtuutus.Core.Observability;

/// <summary>
/// Spans on the <see cref="DefaultActivitySource.SourceName"/> source: one per engine call, one per
/// database round trip it makes, and one per write, delete or cache lookup. Nothing is allocated
/// while no listener samples the source.
/// </summary>
public static class ValtuutusTracing
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
    internal const string DbSystemName = "db.system.name";
    internal const string ErrorType = "error.type";

    private static readonly AsyncLocal<RequestTrace?> CurrentRequest = new();

    /// <summary>
    /// Whether anything listens to the source. Callers check it before building data that only a
    /// span would carry.
    /// </summary>
    public static bool Enabled => DefaultActivitySource.Instance.HasListeners();

    /// <summary>
    /// Starts the span of an engine call and makes it the request every round trip started under it
    /// reports to. Synchronous on purpose: the request stays current for the rest of the calling
    /// async method and everything it starts, including work still running after it returns.
    /// </summary>
    internal static RequestTrace? StartRequest(string name, SnapToken? requestedSnapToken)
    {
        if (!Enabled) return null;
        var activity = DefaultActivitySource.Instance.StartActivity(name, ActivityKind.Internal);
        if (activity is null) return null;
        activity.SetTag(SnapTokenSource, requestedSnapToken is null ? "latest" : "request");
        var trace = new RequestTrace(activity);
        CurrentRequest.Value = trace;
        return trace;
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
        if (!Enabled) return default;
        return StartRoundTrip(dbSystem, [query], resolvesSnapToken: false);
    }

    /// <summary>Starts the span of one database round trip that runs several queries in one batch.</summary>
    /// <param name="dbSystem">OpenTelemetry <c>db.system.name</c> of the provider.</param>
    /// <param name="queries">The kind of each query in the batch, in order.</param>
    /// <param name="resolvesSnapToken">Whether the batch also resolves the request's latest snap token.</param>
    public static ValtuutusSpan StartRoundTrip(string? dbSystem, string[] queries, bool resolvesSnapToken)
    {
        if (!Enabled) return default;
        var activity = DefaultActivitySource.Instance.StartActivity("valtuutus.round_trip", ActivityKind.Client);
        var request = CurrentRequest.Value;
        request?.CountRoundTrip();
        if (activity is null) return default;

        if (dbSystem is not null) activity.SetTag(DbSystemName, dbSystem);
        activity.SetTag(Queries, queries);
        activity.SetTag(QueryCount, queries.Length);
        if (resolvesSnapToken) activity.SetTag(ResolvesSnapToken, true);
        if (request is { Step: > 0 and var step }) activity.SetTag(Step, step);
        return new ValtuutusSpan(activity, request);
    }

    /// <summary>Starts the span of a write or delete, named after the calling provider method.</summary>
    /// <param name="dbSystem">OpenTelemetry <c>db.system.name</c> of the provider; null when it is
    /// not a database.</param>
    /// <param name="operation"><c>Write</c> or <c>Delete</c>.</param>
    public static ValtuutusSpan StartWrite(string? dbSystem, [CallerMemberName] string operation = "")
    {
        if (!Enabled) return default;
        var activity = DefaultActivitySource.Instance.StartActivity(
            operation == "Delete" ? "valtuutus.delete" : "valtuutus.write", ActivityKind.Client);
        if (activity is null) return default;
        if (dbSystem is not null) activity.SetTag(DbSystemName, dbSystem);
        return new ValtuutusSpan(activity, null);
    }

    /// <summary>Starts the span of a cached engine call.</summary>
    /// <param name="operation">The engine call: <c>check</c>, <c>subject_permission</c>,
    /// <c>lookup_entity</c> or <c>lookup_subject</c>.</param>
    public static ValtuutusSpan StartCache(string operation)
    {
        if (!Enabled) return default;
        var activity = DefaultActivitySource.Instance.StartActivity("valtuutus.cache", ActivityKind.Internal);
        if (activity is null) return default;
        activity.SetTag(Operation, operation);
        return new ValtuutusSpan(activity, null);
    }

    internal static bool RecordFailure(Activity activity, Exception e)
    {
        activity.SetStatus(ActivityStatusCode.Error, e.Message);
        activity.SetTag(ErrorType, e.GetType().FullName);
        return false;
    }
}

/// <summary>
/// A round trip, write, delete or cache span; disposing it ends the span. The default value is the
/// span that was not started because nothing listens.
/// </summary>
public readonly struct ValtuutusSpan : IDisposable
{
    private readonly Activity? _activity;
    private readonly RequestTrace? _request;

    internal ValtuutusSpan(Activity activity, RequestTrace? request)
    {
        _activity = activity;
        _request = request;
    }

    /// <summary>Records the snap token the write or delete committed at.</summary>
    public void SetSnapToken(SnapToken snapToken) => _activity?.SetTag(ValtuutusTracing.SnapTokenValue, snapToken.Value);

    /// <summary>Records whether the cache answered the call.</summary>
    public void SetCacheHit(bool hit) => _activity?.SetTag(ValtuutusTracing.CacheHit, hit);

    /// <summary>
    /// Marks the span failed. Returns false, so it can sit in an exception filter that records the
    /// failure without catching it.
    /// </summary>
    public bool RecordFailure(Exception e) => _activity is not null && ValtuutusTracing.RecordFailure(_activity, e);

    /// <summary>
    /// Marks the moment the database responded, for a round trip whose results are handed to the
    /// engine while they are still being read: the engine can answer from the first of them before
    /// the span ends, which does not make this round trip a straggler.
    /// </summary>
    public void Responded()
    {
        if (_activity is null) return;
        _activity.SetTag(ValtuutusTracing.Straggler, _request is { Answered: true });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_activity is null) return;
        switch (_activity.GetTagItem(ValtuutusTracing.Straggler))
        {
            // The engine already returned its answer: nobody reads what this round trip brings back.
            case null when _request is { Answered: true }:
                _activity.SetTag(ValtuutusTracing.Straggler, true);
                break;
            // Decided when the database responded; the tag is only kept when true.
            case false:
                _activity.SetTag(ValtuutusTracing.Straggler, null);
                break;
        }
        _activity.Dispose();
    }
}

/// <summary>The span of one engine call, and what the round trips under it report back to it.</summary>
internal sealed class RequestTrace(Activity activity) : IDisposable
{
    private int _roundTrips;
    private volatile bool _answered;

    public int Step { get; private set; }

    public bool Answered => _answered;

    public void CountRoundTrip() => Interlocked.Increment(ref _roundTrips);

    public void NextStep() => Step++;

    public void SetTag(string name, object? value) => activity.SetTag(name, value);

    /// <summary>Records what every engine call reports once it has its answer.</summary>
    public void Answer(SnapToken? snapToken)
    {
        _answered = true;
        if (snapToken is { } token) activity.SetTag(ValtuutusTracing.SnapTokenValue, token.Value);
        activity.SetTag(ValtuutusTracing.RoundTrips, Volatile.Read(ref _roundTrips));
        if (Step > 0) activity.SetTag(ValtuutusTracing.Steps, Step);
    }

    /// <summary>Marks the call failed; false, for use in an exception filter.</summary>
    public bool Fail(Exception e) => ValtuutusTracing.RecordFailure(activity, e);

    public void Dispose()
    {
        _answered = true;
        activity.Dispose();
    }
}
