using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupSubject.V2;

/// <summary>
/// Turns a lookup request into its graph of queries and set operations. Decides what to ask; the
/// engine and its query runner own execution and the snap token. A dependent hop builds its
/// continuation once the ids it starts from are known.
/// </summary>
internal sealed class LookupSubjectGraphBuilder(Schema schema, LookupSubjectRequest request)
{
    private string FinalSubjectType => request.SubjectType;

    public GraphNode Build(string entityType, string permission, IReadOnlyCollection<string> entityIds, int depth)
    {
        // An empty id set must never reach the data layer, where an entity-id filter without ids
        // filters nothing and would return every subject.
        if (depth == 0 || entityIds.Count == 0) return GraphNode.None;

        var remaining = depth - 1;
        return schema.GetRelationType(entityType, permission) switch
        {
            RelationType.DirectRelation =>
                Relation(entityType, schema.GetRelation(entityType, permission), entityIds, remaining),
            RelationType.Permission =>
                Permission(entityType, schema.GetPermission(entityType, permission).Tree, entityIds, remaining),
            RelationType.Attribute => Attribute(entityType, permission, entityIds),
            _ => throw new InvalidOperationException(),
        };
    }

    private GraphNode Permission(string entityType, PermissionNode node, IReadOnlyCollection<string> entityIds, int depth)
    {
        if (node.Type == PermissionNodeType.Leaf) return Leaf(entityType, node.LeafNode!, entityIds, depth);

        var operation = node.ExpressionNode!;
        return operation.Operation switch
        {
            PermissionOperation.Union => Children(entityType, operation.Children, entityIds, depth, union: true),
            PermissionOperation.Intersect => Children(entityType, operation.Children, entityIds, depth, union: false),
            PermissionOperation.Negate => new NegateNode(
                Permission(entityType, operation.Children[0], entityIds, depth),
                granted => new LookupSubjectQuery
                {
                    Kind = LookupSubjectQueryKind.SubjectsExcluding, SubjectType = FinalSubjectType, Ids = granted,
                }),
            _ => throw new InvalidOperationException(),
        };
    }

    private GraphNode Children(string entityType, List<PermissionNode> children, IReadOnlyCollection<string> entityIds,
        int depth, bool union)
    {
        // A child that can never reach the final subject type contributes nothing: it empties an
        // intersection and drops out of a union.
        var live = new List<PermissionNode>(children.Count);
        foreach (var child in children)
        {
            if (!IsStaticallyDead(entityType, child)) live.Add(child);
            else if (!union) return GraphNode.None;
        }
        if (live.Count == 0) return GraphNode.None;

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
                SubjectType = FinalSubjectType, Ids = entityIds,
            }
            : null;

        var nodes = new GraphNode[live.Count];
        for (var i = 0; i < live.Count; i++)
        {
            nodes[i] = shared is not null && BatchableRelation(entityType, live[i]) is { } relation
                ? new QueryNode(shared, result => SubjectSet.FromTuples((List<RelationTuple>)result, relation))
                : Permission(entityType, live[i], entityIds, depth);
        }
        return new SetNode(nodes, union);
    }

    private GraphNode Leaf(string entityType, PermissionNodeLeaf leaf, IReadOnlyCollection<string> entityIds, int depth)
    {
        if (leaf.Type == PermissionNodeLeafType.Expression) return Expression(entityType, leaf.ExpressionNode!, entityIds);

        var reference = leaf.PermissionNode!;
        return reference.IsIndirect
            ? TupleToUserSet(entityType, reference.UserSet!, reference.ComputedUserSet!, entityIds, depth)
            : Build(entityType, reference.Permission, entityIds, depth);
    }

    private GraphNode Expression(string entityType, PermissionNodeLeafExp expression, IReadOnlyCollection<string> entityIds)
    {
        var function = schema.Functions[expression.FunctionName] ?? throw new InvalidOperationException();
        if (!expression.IsContextValid(request.Context)) return GraphNode.None;

        var query = new LookupSubjectQuery
        {
            Kind = LookupSubjectQueryKind.Attributes, EntityType = entityType,
            Names = expression.AttributeArgNames, Ids = entityIds,
        };
        return new QueryNode(query, result =>
        {
            var attributes = (Dictionary<(string AttributeName, string EntityId), AttributeTuple>)result;
            foreach (var attribute in attributes.Values)
            {
                using var arguments = function.BuildArgs(expression.Args,
                    static (arg, paramType, state) =>
                        state.attributes.TryGetValue((arg.AttributeName, state.entityId), out var value)
                            ? value.GetValue(state.schema.GetAttribute(state.entityType, arg.AttributeName).Type)
                            : new LiteralValueUnion { LiteralType = paramType },
                    (attributes, entityId: attribute.EntityId, entityType, schema),
                    request.Context);
                if (function.Lambda(arguments.Span)) return SubjectSet.Gate(true);
            }
            return SubjectSet.Gate(false);
        });
    }

    private static GraphNode Attribute(string entityType, string attribute, IReadOnlyCollection<string> entityIds) =>
        new QueryNode(
            new LookupSubjectQuery
            {
                Kind = LookupSubjectQueryKind.Attribute, EntityType = entityType, Names = [attribute], Ids = entityIds,
            },
            static result => SubjectSet.Gate(((List<AttributeTuple>)result)
                .Exists(a => a.Value.TryGetValue<bool>(out var value) && value)));

    private GraphNode TupleToUserSet(string entityType, string tupleSetRelation, string computedRelation,
        IReadOnlyCollection<string> entityIds, int depth)
    {
        var relation = schema.GetRelation(entityType, tupleSetRelation);
        var nodes = new GraphNode[relation.Entities.Count];
        for (var i = 0; i < nodes.Length; i++)
        {
            var target = relation.Entities[i];
            nodes[i] = target.Relation is null && depth > 0 && IsTerminalFor(target.Type, computedRelation)
                ? Joined(entityType, relation.Name, entityIds, target.Type, computedRelation)
                : new ExpandNode(
                    new LookupSubjectQuery
                    {
                        Kind = LookupSubjectQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                        SubjectType = target.Type, SubjectRelation = target.Relation, Ids = entityIds,
                    },
                    next => Build(target.Type, computedRelation, next, depth));
        }
        return new SetNode(nodes, union: true);
    }

    private GraphNode Relation(string entityType, Relation relation, IReadOnlyCollection<string> entityIds, int depth)
    {
        if (!relation.EntityTypes.Contains(FinalSubjectType) && !relation.HasSubRelationPaths) return GraphNode.None;

        var nodes = new List<GraphNode>(relation.Entities.Count);
        foreach (var target in relation.Entities)
        {
            if (target.Type == FinalSubjectType)
            {
                nodes.Add(new QueryNode(
                    new LookupSubjectQuery
                    {
                        Kind = LookupSubjectQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                        SubjectType = FinalSubjectType, Ids = entityIds,
                    },
                    ToSubjects));
                continue;
            }

            if (target.Relation is null) continue;
            var subRelation = schema.GetRelation(target.Type, target.Relation);
            if (!subRelation.HasSubRelationPaths && subRelation.EntityTypes.Contains(FinalSubjectType))
            {
                nodes.Add(Joined(entityType, relation.Name, entityIds, target.Type, target.Relation));
                continue;
            }

            // A userset hop re-enters Relation directly, so it applies Build's depth rule here: a
            // userset chain, or a cycle of usersets in the data, stops once the depth is spent.
            nodes.Add(new ExpandNode(
                new LookupSubjectQuery
                {
                    Kind = LookupSubjectQueryKind.Relations, EntityType = entityType, Relation = relation.Name,
                    SubjectType = target.Type, SubjectRelation = subRelation.Name, Ids = entityIds,
                },
                next => depth == 0 ? GraphNode.None : Relation(target.Type, subRelation, next, depth - 1)));
        }
        return new SetNode(nodes.ToArray(), union: true);
    }

    private static GraphNode Joined(string entityType, string relation, IReadOnlyCollection<string> entityIds,
        string subEntityType, string subRelation) =>
        new QueryNode(
            new LookupSubjectQuery
            {
                Kind = LookupSubjectQueryKind.Joined, EntityType = entityType, Relation = relation, Ids = entityIds,
                SubEntityType = subEntityType, SubjectRelation = subRelation,
            },
            ToSubjects);

    private static SubjectSet ToSubjects(object result) => SubjectSet.FromTuples((List<RelationTuple>)result);

    private bool IsTerminalFor(string entityType, string relationName)
    {
        if (schema.GetRelationType(entityType, relationName) != RelationType.DirectRelation) return false;
        var relation = schema.GetRelation(entityType, relationName);
        return !relation.HasSubRelationPaths && relation.EntityTypes.Contains(FinalSubjectType);
    }

    private bool IsStaticallyDead(string entityType, PermissionNode child) =>
        child is { Type: PermissionNodeType.Leaf, LeafNode: { Type: PermissionNodeLeafType.Permission, PermissionNode: { IsIndirect: false } reference } }
        && !schema.CanSubjectTypeReach(entityType, reference.Permission, FinalSubjectType);

    private string? BatchableRelation(string entityType, PermissionNode child) =>
        child is { Type: PermissionNodeType.Leaf, LeafNode: { Type: PermissionNodeLeafType.Permission, PermissionNode: { IsIndirect: false } reference } }
        && IsTerminalFor(entityType, reference.Permission)
            ? reference.Permission
            : null;
}
