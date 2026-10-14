using System.Collections.Concurrent;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// Compiled lookup plans, one per entity type, permission/relation/attribute name and subject type,
/// each passed through the registered <see cref="ILookupSubjectPlanRewriter"/>s once. A singleton
/// next to the <see cref="Schema"/> singleton, so plans live exactly as long as the schema they
/// were compiled from.
/// </summary>
internal sealed class LookupSubjectPlanCache(Schema schema, IEnumerable<ILookupSubjectPlanRewriter>? rewriters = null)
{
    private readonly ILookupSubjectPlanRewriter[] _rewriters = rewriters?.ToArray() ?? [];
    private readonly ConcurrentDictionary<PlanKey, LookupPlan> _plans = new();

    public LookupPlan GetOrCompile(string entityType, string name, string subjectType) =>
        _plans.GetOrAdd(new PlanKey(entityType, name, subjectType),
            static (key, state) =>
            {
                var plan = new LookupSubjectPlanCompiler(state.Schema, key.SubjectType).Compile(key.EntityType, key.Name);
                foreach (var rewriter in state.Rewriters)
                    plan = rewriter.Rewrite(plan, state.Schema, key.EntityType, key.Name, key.SubjectType);
                return plan;
            },
            (Schema: schema, Rewriters: _rewriters));

    private readonly record struct PlanKey(string EntityType, string Name, string SubjectType);
}
