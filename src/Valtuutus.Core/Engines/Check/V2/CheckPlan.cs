using System.Collections.Concurrent;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal sealed record CheckPlan(PlanNode Root, int SlotCount);

// Registered as a DI singleton next to the Schema singleton: a schema reload produces a new
// container/schema and therefore a fresh cache, which is what stands in for `schemaVersion`
// in the design doc's cache key.
internal sealed class CheckPlanCache(Schema schema)
{
    private readonly ConcurrentDictionary<PlanKey, CheckPlan> _plans = new();

    public CheckPlan GetOrCompile(string entityType, string permission, string? subjectType)
        => _plans.GetOrAdd(new PlanKey(entityType, permission, subjectType),
            static (key, s) => PlanCompiler.Compile(s, key.EntityType, key.Permission, key.SubjectType),
            schema);

    private readonly record struct PlanKey(string EntityType, string Permission, string? SubjectType);
}
