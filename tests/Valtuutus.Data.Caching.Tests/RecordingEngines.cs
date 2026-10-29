using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;

namespace Valtuutus.Data.Caching.Tests;

// Answers at the given token when the request carries none, and at the request's token otherwise.
internal sealed class RecordingCheckEngine(SnapToken? snapToken, Func<Task>? onCall = null) : ICheckEngine
{
    public List<CheckRequest> Requests { get; } = [];
    public List<SubjectPermissionRequest> SubjectPermissionRequests { get; } = [];

    public async ValueTask<CheckResult> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        Requests.Add(req);
        if (onCall is not null) await onCall();
        return new CheckResult { Allowed = true, SnapToken = req.SnapToken ?? snapToken };
    }

    public ValueTask<SubjectPermissionResult> SubjectPermission(SubjectPermissionRequest req,
        CancellationToken cancellationToken)
    {
        SubjectPermissionRequests.Add(req);
        return new(new SubjectPermissionResult
            { Permissions = new() { ["view"] = true }, SnapToken = req.SnapToken ?? snapToken });
    }

    public ValueTask<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

internal sealed class RecordingLookupEntityEngine(SnapToken snapToken) : ILookupEntityEngine
{
    public List<LookupEntityRequest> Requests { get; } = [];

    public ValueTask<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        Requests.Add(req);
        return new(new LookupEntityPage(["1"], null) { SnapToken = req.SnapToken ?? snapToken });
    }
}

internal sealed class RecordingLookupSubjectEngine(SnapToken snapToken) : ILookupSubjectEngine
{
    public List<LookupSubjectRequest> Requests { get; } = [];

    public ValueTask<LookupSubjectResult> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken)
    {
        Requests.Add(req);
        return new(new LookupSubjectResult { SubjectIds = ["alice"], SnapToken = req.SnapToken ?? snapToken });
    }
}
