using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

// Provider-owned execution of a fused subtree (design doc, "IR" / "Physical escape hatch").
// Instances are created at plan-rewrite time and cached inside the plan — they must be
// immutable and must not capture request-scoped state; runtime bindings (entity, subject,
// snapshot) arrive as Execute arguments.
internal interface ICheckOp
{
    ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx,
        string entityType, string entityId, CancellationToken ct);

    // Explain fidelity: a fused op must be able to say what it replaced.
    string Describe();
}

// The rewrite seam: implementations registered in DI run over every compiled plan
// (CheckPlanCache applies them in registration order, once per plan key). Contract:
// return unrecognized nodes unchanged; never unwrap a MemoNode — its child is shared by
// multiple parents and fusing it into one duplicates work for the others (design doc,
// "MemoNode barrier rule"); implementations must be stateless/thread-safe (one singleton
// serves concurrent plan compiles).
internal interface IPlanRewriter
{
    PlanNode Rewrite(PlanNode root, Schema schema);
}
