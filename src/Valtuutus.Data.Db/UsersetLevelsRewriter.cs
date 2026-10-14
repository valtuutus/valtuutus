using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.Db;

/// <summary>
/// Reads userset chains several levels per query: a direct relation with a userset that is not
/// terminal, one step per level as compiled, becomes a <see cref="UsersetLevelsPlan"/> of
/// <see cref="IValtuutusDbOptions.LookupHopsPerQuery"/> levels. Relations whose usersets are all
/// terminal already take one step through the joined query and are left alone. The new query kind
/// runs only through the provider's lookup batch commands, so without them nothing changes.
/// </summary>
internal sealed class UsersetLevelsRewriter(IValtuutusDbOptions options, ILookupSubjectBatchOps? lookupBatchOps = null)
    : ILookupSubjectPlanRewriter
{
    private readonly int _levels = lookupBatchOps is null ? 1 : options.LookupHopsPerQuery;

    public LookupPlan Rewrite(LookupPlan root, Schema schema, string entityType, string name, string subjectType)
    {
        if (_levels < 2
            || root is not SetPlan set
            || schema.GetRelationType(entityType, name) != RelationType.DirectRelation
            || !set.Children.Any(child => child is ExpandPlan))
            return root;

        return new UsersetLevelsPlan(
            new LookupSubjectQuery
            {
                Kind = LookupSubjectQueryKind.UsersetLevels, EntityType = entityType, Relation = name,
                SubjectType = subjectType,
            },
            _levels, root);
    }
}
