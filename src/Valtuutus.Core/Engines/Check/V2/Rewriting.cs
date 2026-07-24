using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

/// <summary>
/// Provider-owned execution of a fused subtree. Instances are created at plan-rewrite time
/// and cached inside the plan — they MUST be immutable and MUST NOT capture request-scoped
/// state; runtime bindings (entity, subject, snapshot, reader) arrive as arguments.
/// </summary>
public interface ICheckOp
{
    /// <summary>
    /// Evaluates the fused subtree for one entity: does the request's subject hold what this
    /// op replaced, as of the request's snapshot?
    /// </summary>
    ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx,
        string entityType, string entityId, CancellationToken ct);

    /// <summary>
    /// Human-readable description of what this op replaced, for explain/diagnostic output —
    /// a fused op must be able to say what it stands in for.
    /// </summary>
    string Describe();
}

/// <summary>
/// The rewrite seam: implementations registered in DI run over every compiled plan, in
/// registration order, once per plan key (entityType, permission, subjectType). Contract:
/// return unrecognized nodes unchanged (and reference-identical, to preserve interning);
/// NEVER unwrap a <see cref="MemoNode"/> — its child is shared by multiple parents and
/// fusing it into one duplicates work for the others; implementations must be
/// stateless/thread-safe (one singleton serves concurrent plan compiles).
/// </summary>
public interface IPlanRewriter
{
    /// <param name="root">The compiled plan's root node.</param>
    /// <param name="schema">The schema the plan was compiled from.</param>
    /// <param name="entityType">The plan key's entity type — schema lookups for node
    /// classification need it.</param>
    /// <param name="subjectType">The plan key's subject type, or null when unknown at
    /// compile time. Rewrites may specialize on it: semantics preservation is per plan
    /// key, not universal.</param>
    /// <returns>The rewritten root, or the same instance when nothing was recognized.</returns>
    PlanNode Rewrite(PlanNode root, Schema schema, string entityType, string? subjectType);
}
