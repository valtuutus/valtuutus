using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.Db;

/// <summary>
/// Walks self-recursive relations several hops per query: a <see cref="FixpointPlan"/>, always the
/// root of its relation's plan, becomes a <see cref="FixpointLevelsPlan"/> of
/// <see cref="IValtuutusDbOptions.LookupHopsPerQuery"/> hops, whose walks with no joined grants read
/// their direct grants within the first query. The new query kinds run only through the provider's
/// lookup batch commands, so without them nothing changes.
/// </summary>
internal sealed class FixpointLevelsRewriter(IValtuutusDbOptions options, ILookupEntityBatchOps? lookupBatchOps = null)
    : ILookupEntityPlanRewriter
{
    private readonly int _levels = lookupBatchOps is null ? 1 : options.LookupHopsPerQuery;

    public LookupEntityPlan Rewrite(LookupEntityPlan root, Schema schema, string entityType, string name,
        string subjectType)
    {
        if (_levels < 2 || root is not FixpointPlan fixpoint) return root;

        return new FixpointLevelsPlan(
            new LookupEntityQuery
            {
                Kind = LookupEntityQueryKind.RelationsLevels, EntityType = entityType, Relation = name,
            },
            _levels, fixpoint,
            new LookupEntityQuery
            {
                Kind = LookupEntityQueryKind.RelationsLevelsFromSubject, EntityType = entityType, Relation = name,
                SubjectType = subjectType,
            });
    }
}
