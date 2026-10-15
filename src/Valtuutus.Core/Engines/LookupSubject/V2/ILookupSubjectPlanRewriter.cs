using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// Replaces parts of a compiled LookupSubject plan with shapes a data provider answers better.
/// Implementations registered in DI run over every compiled plan, in registration order, once per
/// plan key (entity type, name, subject type), and the result is cached with the key.
/// Contract: return every node not recognized unchanged and reference-identical; keep no state, as
/// one instance serves concurrent compiles; never change which subjects a plan grants.
/// </summary>
public interface ILookupSubjectPlanRewriter
{
    /// <param name="root">The compiled plan, or a previous rewriter's result.</param>
    /// <param name="schema">The schema the plan was compiled from.</param>
    /// <param name="entityType">The plan key's entity type.</param>
    /// <param name="name">The plan key's permission, relation or attribute name.</param>
    /// <param name="subjectType">The plan key's subject type.</param>
    /// <returns>The rewritten plan, or <paramref name="root"/> when nothing was recognized.</returns>
    LookupPlan Rewrite(LookupPlan root, Schema schema, string entityType, string name, string subjectType);
}
