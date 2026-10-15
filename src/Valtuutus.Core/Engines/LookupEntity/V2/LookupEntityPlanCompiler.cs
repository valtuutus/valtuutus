using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupEntity.V2;

/// <summary>
/// Compiles the plan for one entity type's permission, relation or attribute looked up for one
/// subject type. Settles everything the schema alone decides: branches that can never reach the
/// subject type, sibling relations fetched together, joined reads for terminal usersets and
/// tuple-to-usersets, and which intersection shape applies. References to other permissions and
/// hops into other entity types name their target, which is compiled on its own when a request
/// first reaches it.
/// </summary>
internal sealed class LookupEntityPlanCompiler(Schema schema, string subjectType)
{
    public LookupEntityPlan Compile(string entityType, string name) =>
        schema.GetRelationType(entityType, name) switch
        {
            RelationType.DirectRelation => Relation(entityType, schema.GetRelation(entityType, name)),
            RelationType.Permission => Permission(entityType, schema.GetPermission(entityType, name).Tree),
            RelationType.Attribute => new AttributePlan(new LookupEntityQuery
            {
                Kind = LookupEntityQueryKind.Attribute, EntityType = entityType, Names = [name],
            }),
            _ => throw new InvalidOperationException(),
        };

    private LookupEntityPlan Permission(string entityType, PermissionNode node)
    {
        if (node.Type == PermissionNodeType.Leaf) return Leaf(entityType, node.LeafNode!);

        var operation = node.ExpressionNode!;
        return operation.Operation switch
        {
            PermissionOperation.Union => Children(entityType, operation.Children, union: true),
            PermissionOperation.Intersect => Children(entityType, operation.Children, union: false),
            PermissionOperation.Negate => new NegatePlan(Permission(entityType, operation.Children[0]), entityType),
            _ => throw new InvalidOperationException(),
        };
    }

    private LookupEntityPlan Children(string entityType, List<PermissionNode> children, bool union)
    {
        // A child that can never reach the subject type contributes nothing: it empties an
        // intersection and drops out of a union.
        var live = new List<PermissionNode>(children.Count);
        foreach (var child in children)
        {
            if (!IsStaticallyDead(entityType, child)) live.Add(child);
            else if (!union) return LookupEntityPlan.Empty;
        }
        if (live.Count == 0) return LookupEntityPlan.Empty;

        // Plain direct-relation siblings share one multi-relation query; each name's first
        // occurrence reads its rows from it, a repeat resolves as a reference.
        List<string>? siblings = null;
        foreach (var child in live)
        {
            if (BatchableRelation(entityType, child) is { } name && !(siblings?.Contains(name) ?? false))
                (siblings ??= []).Add(name);
        }
        var shared = siblings is { Count: >= 2 }
            ? new LookupEntityQuery
            {
                Kind = LookupEntityQueryKind.RelationsMulti, EntityType = entityType, Names = siblings.ToArray(),
                SubjectType = subjectType,
            }
            : null;

        var sliced = shared is null ? null : new HashSet<string>();
        LookupEntityPlan Child(PermissionNode child) =>
            sliced is not null && BatchableRelation(entityType, child) is { } relation && sliced.Add(relation)
                ? new SlicePlan(relation)
                : Permission(entityType, child);

        if (!union && HasMixedAttributeAndOtherChildren(live))
        {
            var others = new List<LookupEntityPlan>(live.Count);
            var checks = new List<ExpressionPlan>(2);
            foreach (var child in live)
            {
                if (IsExpressionLeaf(child)) checks.Add(Expression(entityType, child.LeafNode!.ExpressionNode!));
                else others.Add(Child(child));
            }
            return new ConstrainedIntersectPlan(others.ToArray(), shared, checks.ToArray());
        }

        if (!union && HasNegateAndPositiveChildren(live))
        {
            var positives = new List<LookupEntityPlan>(live.Count);
            var negated = new List<LookupEntityPlan>(live.Count);
            foreach (var child in live)
            {
                if (IsNegate(child)) negated.Add(Permission(entityType, child.ExpressionNode!.Children[0]));
                else positives.Add(Child(child));
            }
            return new NegateIntersectPlan(positives.ToArray(), shared, negated.ToArray());
        }

        var plans = new LookupEntityPlan[live.Count];
        for (var i = 0; i < live.Count; i++)
            plans[i] = Child(live[i]);
        return new SetPlan(plans, union, shared);
    }

    private LookupEntityPlan Leaf(string entityType, PermissionNodeLeaf leaf)
    {
        if (leaf.Type == PermissionNodeLeafType.Expression) return Expression(entityType, leaf.ExpressionNode!);

        var reference = leaf.PermissionNode!;
        return reference.IsIndirect
            ? TupleToUserSet(entityType, reference.UserSet!, reference.ComputedUserSet!)
            : new RefPlan(entityType, reference.Permission);
    }

    private ExpressionPlan Expression(string entityType, PermissionNodeLeafExp expression) =>
        new(new LookupEntityQuery
            {
                Kind = LookupEntityQueryKind.Attributes, EntityType = entityType, Names = expression.AttributeArgNames,
            },
            expression,
            schema.Functions[expression.FunctionName] ?? throw new InvalidOperationException(),
            schema);

    private LookupEntityPlan TupleToUserSet(string entityType, string tupleSetRelation, string computedRelation)
    {
        var relation = schema.GetRelation(entityType, tupleSetRelation);
        var plans = new LookupEntityPlan[relation.Entities.Count];
        for (var i = 0; i < plans.Length; i++)
        {
            var target = relation.Entities[i];
            plans[i] = target.Relation is null && IsTerminalFor(target.Type, computedRelation)
                ? Joined(entityType, relation.Name, target.Type, subjectRelation: "", computedRelation)
                : new ExpandPlan(
                    new LookupEntityQuery
                    {
                        Kind = LookupEntityQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                        SubjectType = target.Type, SubjectRelation = target.Relation ?? "",
                    },
                    target.Type, computedRelation);
        }
        return new SetPlan(plans, union: true, shared: null);
    }

    private LookupEntityPlan Relation(string entityType, Relation relation)
    {
        if (!relation.EntityTypes.Contains(subjectType) && !relation.HasSubRelationPaths) return LookupEntityPlan.Empty;

        var plans = new List<LookupEntityPlan>(relation.Entities.Count);
        foreach (var target in relation.Entities)
        {
            if (target.Type == subjectType && target.Relation is null)
            {
                plans.Add(new SubjectBoundPlan(
                    new LookupEntityQuery
                    {
                        Kind = LookupEntityQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                        SubjectType = subjectType,
                    },
                    isHop: false));
                continue;
            }

            if (target.Relation is null) continue;
            var subRelation = schema.GetRelation(target.Type, target.Relation);
            if (!subRelation.HasSubRelationPaths && subRelation.EntityTypes.Contains(subjectType))
            {
                plans.Add(Joined(entityType, relation.Name, target.Type, target.Relation, target.Relation));
                continue;
            }

            // A userset chain continues on the target relation's plan; a cycle of usersets across
            // relations re-enters until the request's depth is spent.
            plans.Add(new ExpandPlan(
                new LookupEntityQuery
                {
                    Kind = LookupEntityQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                    SubjectType = target.Type, SubjectRelation = target.Relation,
                },
                target.Type, target.Relation));
        }
        return SelfRecursion(entityType, relation.Name, plans) ?? new SetPlan(plans.ToArray(), union: true, shared: null);
    }

    // A relation whose only userset hop re-enters itself is walked as a fixpoint; any hop into
    // another relation keeps the unrolled shape.
    private static LookupEntityPlan? SelfRecursion(string entityType, string relation, List<LookupEntityPlan> plans)
    {
        ExpandPlan? self = null;
        var direct = new List<SubjectBoundPlan>();
        var joined = new List<SubjectBoundPlan>();
        foreach (var plan in plans)
        {
            switch (plan)
            {
                case ExpandPlan expand when expand.EntityType == entityType && expand.Name == relation && self is null:
                    self = expand;
                    break;
                case SubjectBoundPlan { IsHop: false } bound:
                    direct.Add(bound);
                    break;
                case SubjectBoundPlan bound:
                    joined.Add(bound);
                    break;
                default:
                    return null;
            }
        }
        return self is null ? null : new FixpointPlan(direct.ToArray(), joined.ToArray(), self.Main);
    }

    private SubjectBoundPlan Joined(string entityType, string relation, string subEntityType, string subjectRelation,
        string subRelation) =>
        new(new LookupEntityQuery
            {
                Kind = LookupEntityQueryKind.Joined, EntityType = entityType, Relation = relation,
                SubEntityType = subEntityType, SubjectRelation = subjectRelation, SubRelation = subRelation,
                SubjectType = subjectType,
            },
            isHop: true);

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

    private static bool IsExpressionLeaf(PermissionNode child) =>
        child is { Type: PermissionNodeType.Leaf, LeafNode.Type: PermissionNodeLeafType.Expression };

    private static bool IsNegate(PermissionNode child) =>
        child is { Type: PermissionNodeType.Expression, ExpressionNode.Operation: PermissionOperation.Negate };

    private static bool HasMixedAttributeAndOtherChildren(List<PermissionNode> children) =>
        children.Exists(IsExpressionLeaf) && !children.TrueForAll(IsExpressionLeaf);

    private static bool HasNegateAndPositiveChildren(List<PermissionNode> children) =>
        children.Exists(IsNegate) && !children.TrueForAll(IsNegate);
}
