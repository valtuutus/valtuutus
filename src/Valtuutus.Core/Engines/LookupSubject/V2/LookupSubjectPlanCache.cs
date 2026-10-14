using System.Collections.Concurrent;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// Compiled lookup plans, one per entity type, permission/relation/attribute name and subject type.
/// A singleton next to the <see cref="Schema"/> singleton, so plans live exactly as long as the
/// schema they were compiled from.
/// </summary>
internal sealed class LookupSubjectPlanCache(Schema schema)
{
    private readonly ConcurrentDictionary<PlanKey, LookupPlan> _plans = new();

    public LookupPlan GetOrCompile(string entityType, string name, string subjectType) =>
        _plans.GetOrAdd(new PlanKey(entityType, name, subjectType),
            static (key, schema) => new LookupSubjectPlanCompiler(schema, key.SubjectType).Compile(key.EntityType, key.Name),
            schema);

    private readonly record struct PlanKey(string EntityType, string Name, string SubjectType);
}
