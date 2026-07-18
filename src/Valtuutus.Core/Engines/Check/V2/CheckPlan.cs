using System.Collections.Concurrent;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal sealed record CheckPlan(PlanNode Root, int SlotCount);

// Registered as a DI singleton next to the Schema singleton: a schema reload produces a new
// container/schema and therefore a fresh cache, which is what stands in for `schemaVersion`
// in the design doc's cache key. Rewriters come from DI (any assembly may register
// IPlanRewriter implementations; Valtuutus.Data.Db registers RelationalPlanRewriter via
// AddDbSetup) and are applied in registration order, once per plan key.
internal sealed class CheckPlanCache(Schema schema, IEnumerable<IPlanRewriter>? rewriters = null)
{
    private readonly Schema _schema = schema;
    private readonly IPlanRewriter[] _rewriters = rewriters?.ToArray() ?? [];
    private readonly ConcurrentDictionary<PlanKey, CheckPlan> _plans = new();

    public CheckPlan GetOrCompile(string entityType, string permission, string? subjectType)
        => _plans.GetOrAdd(new PlanKey(entityType, permission, subjectType),
            static (key, self) =>
            {
                var plan = PlanCompiler.Compile(self._schema, key.EntityType, key.Permission, key.SubjectType);
                foreach (var rewriter in self._rewriters)
                    plan = plan with { Root = rewriter.Rewrite(plan.Root, self._schema) };
                return plan;
            },
            this);

    private readonly record struct PlanKey(string EntityType, string Permission, string? SubjectType);
}
