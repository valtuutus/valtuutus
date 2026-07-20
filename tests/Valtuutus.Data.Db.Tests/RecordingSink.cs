using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.Db.Tests;

// Shared IOpCompletionSink test double — records every completion/failure/payload it receives so
// a test can assert on what a ReadResultAsync call reported, without needing a real
// CheckPlanExecutor wired up. Mirrors Valtuutus.Data.InMemory.Tests' CheckPlanExecutorSpecs
// .RecordingSink (that one lives in a different assembly, so it isn't reusable here directly).
internal sealed class RecordingSink : IOpCompletionSink
{
    public readonly List<(int Token, bool Result)> Completed = [];
    public readonly List<(int Token, Exception Error)> Failed = [];
    public readonly List<(int Token, object Payload)> Payloads = [];
    public void Complete(int token, bool result) { lock (Completed) Completed.Add((token, result)); }
    public void CompleteWithPayload(int token, object payload) { lock (Payloads) Payloads.Add((token, payload)); }
    public void Fail(int token, Exception error) { lock (Failed) Failed.Add((token, error)); }
}
