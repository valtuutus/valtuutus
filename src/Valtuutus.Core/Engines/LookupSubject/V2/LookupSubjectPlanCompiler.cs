using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// Compiles the plan for one entity type's permission, relation or attribute looked up for one
/// subject type. Settles everything the schema alone decides: branches that can never reach the
/// subject type, sibling relations fetched together, and the joined query for terminal usersets.
/// References to other permissions and hops into other entity types name their target, which is
/// compiled on its own when a request first reaches it.
/// </summary>
internal sealed class LookupSubjectPlanCompiler(Schema schema, string subjectType)
{
    public LookupPlan Compile(string entityType, string name) =>
        schema.GetRelationType(entityType, name) switch
        {
            RelationType.DirectRelation => Relation(entityType, schema.GetRelation(entityType, name)),
            RelationType.Permission => Permission(entityType, schema.GetPermission(entityType, name).Tree),
            RelationType.Attribute => new AttributePlan(new LookupSubjectQuery
            {
                Kind = LookupSubjectQueryKind.Attribute, EntityType = entityType, Names = [name],
            }),
            _ => throw new InvalidOperationException(),
        };

    private LookupPlan Permission(string entityType, PermissionNode node)
    {
        if (node.Type == PermissionNodeType.Leaf) return Leaf(entityType, node.LeafNode!);

        var operation = node.ExpressionNode!;
        return operation.Operation switch
        {
            PermissionOperation.Union => Children(entityType, operation.Children, union: true),
            PermissionOperation.Intersect => Children(entityType, operation.Children, union: false),
            PermissionOperation.Negate => new NegatePlan(Permission(entityType, operation.Children[0])),
            _ => throw new InvalidOperationException(),
        };
    }

    private LookupPlan Children(string entityType, List<PermissionNode> children, bool union)
    {
        // A child that can never reach the subject type contributes nothing: it empties an
        // intersection and drops out of a union.
        var live = new List<PermissionNode>(children.Count);
        foreach (var child in children)
        {
            if (!IsStaticallyDead(entityType, child)) live.Add(child);
            else if (!union) return LookupPlan.Empty;
        }
        if (live.Count == 0) return LookupPlan.Empty;

        // Plain direct-relation siblings share one multi-relation query.
        List<string>? siblings = null;
        foreach (var child in live)
        {
            if (BatchableRelation(entityType, child) is { } name && !(siblings?.Contains(name) ?? false))
                (siblings ??= []).Add(name);
        }
        var shared = siblings is { Count: >= 2 }
            ? new LookupSubjectQuery
            {
                Kind = LookupSubjectQueryKind.RelationsMulti, EntityType = entityType, Names = siblings.ToArray(),
                SubjectType = subjectType,
            }
            : null;

        var plans = new LookupPlan[live.Count];
        for (var i = 0; i < live.Count; i++)
        {
            plans[i] = shared is not null && BatchableRelation(entityType, live[i]) is { } relation
                ? new SlicePlan(relation)
                : Permission(entityType, live[i]);
        }
        return new SetPlan(plans, union, shared);
    }

    private LookupPlan Leaf(string entityType, PermissionNodeLeaf leaf)
    {
        if (leaf.Type == PermissionNodeLeafType.Expression) return Expression(entityType, leaf.ExpressionNode!);

        var reference = leaf.PermissionNode!;
        return reference.IsIndirect
            ? TupleToUserSet(entityType, reference.UserSet!, reference.ComputedUserSet!)
            : new RefPlan(entityType, reference.Permission);
    }

    private ExpressionPlan Expression(string entityType, PermissionNodeLeafExp expression) =>
        new(new LookupSubjectQuery
            {
                Kind = LookupSubjectQueryKind.Attributes, EntityType = entityType, Names = expression.AttributeArgNames,
            },
            entityType, expression,
            schema.Functions[expression.FunctionName] ?? throw new InvalidOperationException(),
            schema);

    private LookupPlan TupleToUserSet(string entityType, string tupleSetRelation, string computedRelation)
    {
        var relation = schema.GetRelation(entityType, tupleSetRelation);
        var plans = new LookupPlan[relation.Entities.Count];
        for (var i = 0; i < plans.Length; i++)
        {
            var target = relation.Entities[i];
            plans[i] = target.Relation is null && IsTerminalFor(target.Type, computedRelation)
                ? Joined(entityType, relation.Name, target.Type, computedRelation, isHop: true)
                : new ExpandPlan(
                    new LookupSubjectQuery
                    {
                        Kind = LookupSubjectQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                        SubjectType = target.Type, SubjectRelation = target.Relation,
                    },
                    target.Type, computedRelation);
        }
        return new SetPlan(plans, union: true, shared: null);
    }

    private LookupPlan Relation(string entityType, Relation relation)
    {
        if (!relation.EntityTypes.Contains(subjectType) && !relation.HasSubRelationPaths) return LookupPlan.Empty;

        var plans = new List<LookupPlan>(relation.Entities.Count);
        foreach (var target in relation.Entities)
        {
            if (target.Type == subjectType)
            {
                plans.Add(new SubjectsPlan(
                    new LookupSubjectQuery
                    {
                        Kind = LookupSubjectQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                        SubjectType = subjectType,
                    },
                    isHop: false));
                continue;
            }

            if (target.Relation is null) continue;
            var subRelation = schema.GetRelation(target.Type, target.Relation);
            if (!subRelation.HasSubRelationPaths && subRelation.EntityTypes.Contains(subjectType))
            {
                plans.Add(Joined(entityType, relation.Name, target.Type, target.Relation, isHop: false));
                continue;
            }

            // A userset chain, or a cycle of usersets in the data, re-enters this relation's plan
            // on the next entity type and stops once the request's depth is spent.
            plans.Add(new ExpandPlan(
                new LookupSubjectQuery
                {
                    Kind = LookupSubjectQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                    SubjectType = target.Type, SubjectRelation = subRelation.Name,
                },
                target.Type, subRelation.Name));
        }
        return new SetPlan(plans.ToArray(), union: true, shared: null);
    }

    private static SubjectsPlan Joined(string entityType, string relation, string subEntityType, string subRelation,
        bool isHop) =>
        new(new LookupSubjectQuery
            {
                Kind = LookupSubjectQueryKind.Joined, EntityType = entityType, Relation = relation,
                SubEntityType = subEntityType, SubjectRelation = subRelation,
            },
            isHop);

    private bool IsTerminalFor(string entityType, string relationName)
    {
        if (schema.GetRelationType(entityType, relationName) != RelationType.DirectRelation) return false;
        var relation = schema.GetRelation(entityType, relationName);
        return !relation.HasSubRelationPaths && relation.EntityTypes.Contains(subjectType);
    }

    private bool IsStaticallyDead(string entityType, PermissionNode child) =>
        child is { Type: PermissionNodeType.Leaf, LeafNode: { Type: PermissionNodeLeafType.Permission, PermissionNode: { IsIndirect: false } reference } }
        && !schema.CanSubjectTypeReach(entityType, reference.Permission, subjectType);

    private string? BatchableRelation(string entityType, PermissionNode child) =>
        child is { Type: PermissionNodeType.Leaf, LeafNode: { Type: PermissionNodeLeafType.Permission, PermissionNode: { IsIndirect: false } reference } }
        && IsTerminalFor(entityType, reference.Permission)
            ? reference.Permission
            : null;
}
