using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Microsoft.Extensions.Logging;

namespace Authorizee.Core;

public class LookupEngine(
    Schema schema,
    ILogger<LookupEngine> logger,
    IRelationTupleReader tupleReader,
    IAttributeReader attributeReader)
{
    public async Task<ConcurrentBag<string>> LookupEntity(LookupEntityRequest req, CancellationToken ct)
    {
        var permission = schema.GetPermissions(req.EntityType)
            .FirstOrDefault(x => x.Name.Equals(req.Permission, StringComparison.InvariantCultureIgnoreCase));

        var relation = schema.GetRelations(req.EntityType)
            .FirstOrDefault(x => x.Name.Equals(req.Permission, StringComparison.InvariantCultureIgnoreCase));

        var type = new { permission, relation } switch
        {
            { permission: null, relation: not null } => RelationType.DirectRelation,
            { permission: not null, relation: null } => RelationType.Permission,
            _ => RelationType.None
        };

        var (lookupTree, relationOrAttributes) = type switch
        {
            RelationType.DirectRelation => ExpandRelation(req.EntityType, relation!, req.SubjectType),
            RelationType.Permission => ExpandPermission(req.EntityType, req.Permission, req.SubjectType),
            _ => throw new InvalidOperationException()
        };
        
        var loadedRelations = new Dictionary<Relation, LookupNodeState>();
        var loadedAttributes = new Dictionary<Attribute, LookupNodeState>();
        var relationsValues = new Dictionary<Relation, RelationOrAttributeTuple[]>();
        var attributeValues = new Dictionary<Attribute, RelationOrAttributeTuple[]>();

        var requestedSubjectRelations = relationOrAttributes.Where(x =>
                x.Type == RelationOrAttributeType.Relation
                && x.Relation!.SubjectType == req.SubjectType)
            .Select(x => x.Relation!)
            .ToArray();

        var relationTuplesWithSubject =
            await tupleReader.GetRelations(requestedSubjectRelations
                    .Select(x => new EntityRelationFilter
                    {
                        Relation = x.Name,
                        EntityType = x.EntityType
                    })
                    .ToArray(),
                new SubjectFilter
                {
                    SubjectId = req.SubjectId,
                    SubjectType = req.SubjectType
                }
            );

        foreach (var r in requestedSubjectRelations)
        {
            loadedRelations.Add(r, LookupNodeState.Loaded);

            var relationInstanceTuples = relationTuplesWithSubject
                .Where(t => t.Relation == r.Name
                            && t.EntityType == r.EntityType
                            && t.SubjectType == r.SubjectType
                            && t.SubjectRelation == (r.SubjectRelation ?? string.Empty))
                .Select(x => new RelationOrAttributeTuple(x))
                .ToArray();

            relationsValues.Add(r, relationInstanceTuples);
        }

        async Task<IList<RelationOrAttributeTuple>> walk(LookupNode node)
        {
            async Task<IList<RelationOrAttributeTuple>> handleLeafRelationNode()
            {
                var nodeRelation = node.LeafNode!.Value.Relation!;
                if (loadedRelations.TryGetValue(nodeRelation, out LookupNodeState value) &&
                    value == LookupNodeState.Loaded)
                {
                    return relationsValues[nodeRelation];
                }
                
                // team#member
                var relatedRelation = relationOrAttributes
                    .Where(x =>
                        x.Type == RelationOrAttributeType.Relation
                        && x.Relation!.EntityType == nodeRelation.SubjectType 
                        && x.Relation!.Name == nodeRelation.SubjectRelation)
                    .Select(x => x.Relation!)
                    .FirstOrDefault();

                // organization#member -> project#org
                relatedRelation ??= relationOrAttributes
                    .Where(x => x.Type == RelationOrAttributeType.Relation
                                && x.Relation!.SubjectType == req.SubjectType
                                && x.Relation!.EntityType == nodeRelation.EntityType)
                    .Select(x => x.Relation!)
                    .FirstOrDefault();

                var subjectValues = relatedRelation is null
                    ? []
                    : relationsValues[relatedRelation];

                if (relatedRelation is not null && subjectValues is [])
                {
                    loadedRelations.Add(nodeRelation, LookupNodeState.Loaded);
                    relationsValues.Add(nodeRelation, []);
                    return [];
                }

                var res = (await tupleReader.GetRelations(
                        new EntityRelationFilter
                        {
                            Relation = nodeRelation.Name,
                            EntityType = nodeRelation.EntityType
                        }
                        ,
                        subjectValues.Select(s => new SubjectFilter
                        {
                            SubjectId = s.EntityId,
                            SubjectType = s.EntityType
                        })
                    ))
                    .Select(x => new RelationOrAttributeTuple(x))
                    .ToArray();
                
                loadedRelations.Add(nodeRelation, LookupNodeState.Loaded);
                relationsValues.Add(nodeRelation, res);
                return res;
            }

            async Task<IList<RelationOrAttributeTuple>> handleLeafAttributeNode()
            {
                var nodeAttribute = node.LeafNode!.Value.Attribute!;
                if (loadedAttributes.TryGetValue(nodeAttribute, out LookupNodeState value) &&
                    value == LookupNodeState.Loaded)
                {
                    return attributeValues[nodeAttribute];
                }

                return (await attributeReader.GetAttributes(new AttributeFilter
                    {
                        Attribute = nodeAttribute.Name,
                        EntityType = nodeAttribute.EntityType
                    }))
                    .Select(x => new RelationOrAttributeTuple(x))
                    .ToArray();
            }
            
            async Task<IList<RelationOrAttributeTuple>> handleLeafNode()
            {
                return node.LeafNode!.Value.Type switch
                {
                    RelationOrAttributeType.Attribute => await handleLeafAttributeNode(),
                    RelationOrAttributeType.Relation => await handleLeafRelationNode(),
                    _ => throw new InvalidOperationException()
                };
            }

            async Task<IList<RelationOrAttributeTuple>> handleExpressionNode()
            {
                if (node.ExpressionNode!.Operation == LookupNodeExpressionType.Union)
                {
                    var entities = new List<RelationOrAttributeTuple>();
                    foreach (var child in node.ExpressionNode!.Children)
                    {
                        entities.AddRange(await walk(child));
                    }

                    return entities.ToArray();
                }

                if (node.ExpressionNode!.Operation == LookupNodeExpressionType.Intersect)
                {
                    var overlappingItems = Enumerable.Empty<RelationOrAttributeTuple>();
                    var count = 0;
                    foreach (var child in node.ExpressionNode!.Children)
                    {
                        var items = await walk(child);
                        overlappingItems = count >= 1
                            ? overlappingItems.IntersectBy(
                                items.Select(x => new { Type = x.EntityType, Id = x.EntityId }),
                                x => new { Type = x.EntityType, Id = x.EntityId })
                            : items;

                        count++;
                    }

                    return overlappingItems.ToArray();
                }
                
                if (node.ExpressionNode!.Operation == LookupNodeExpressionType.Join &&
                    node.ExpressionNode.Children is [{ } childA, { } childB])
                {
                    var childAEntities = await walk(childA);
                    var childBEntities = await walk(childB);

                    // Join is always between 2 relationships,
                    // so we can confidently use a.RelationTuple and b.RelationTuple
                    
                    // "A" is always the principal permission,
                    // this is only a convention which is kind fragile
                    // we need to think of a better way of doing this  
                    return childAEntities.Join(
                            childBEntities,
                            a => new { Type = a.RelationTuple!.SubjectType, Id = a.RelationTuple!.SubjectId },
                            b => new { Type = b.RelationTuple!.EntityType, Id = b.RelationTuple!.EntityId },
                            (a, b) => a)
                        .ToArray();
                }

                return [];
            }

            var actionMapper = new Dictionary<LookupNodeType, Func<Task<IList<RelationOrAttributeTuple>>>>()
            {
                { LookupNodeType.Expression, handleExpressionNode },
                { LookupNodeType.Leaf, handleLeafNode }
            };

            return await actionMapper[node.Type]();
        }

        return new ConcurrentBag<string>((await walk(lookupTree))
            .Select(x => x.EntityId)
            .OrderBy(x => x)
            .ToArray());
        
        // ConcurrentBag<string> entityIDs = [];
        //
        // var callback = (string entityId) => { entityIDs.Add(entityId); };
        //
        // var bulkChecker = new ActionBlock<CheckRequest>(async request =>
        // {
        //     var result = await permissionEngine.Check(request, ct);
        //     if (result)
        //     {
        //         callback(request.EntityId);
        //     }
        // }, new ExecutionDataflowBlockOptions
        // {
        //     MaxDegreeOfParallelism = 5
        // });
        //
        // bulkChecker.Complete();
        // await bulkChecker.Completion;
        // return entityIDs;
    }
    
    private (LookupNode, HashSet<RelationOrAttribute>) ExpandPermission(string rootEntityType,
        string rootEntityPermission, string finalSubjectType, LookupExpressionNode? parentNode = null)
    {
        var rootExpression = parentNode ?? new LookupExpressionNode(LookupNodeExpressionType.Union, []);
        var relationSet = new HashSet<RelationOrAttribute>();

        void walkExpression(string entityType, PermissionNode permNode, LookupExpressionNode lookupNode)
        {
            switch (permNode.Type)
            {
                case PermissionNodeType.Expression:
                    var lookupOperationNode = permNode.ExpressionNode!.Operation switch
                    {
                        PermissionOperation.Intersect => new LookupExpressionNode(LookupNodeExpressionType.Intersect, []),
                        PermissionOperation.Union => new LookupExpressionNode(LookupNodeExpressionType.Union, []),
                        _ => throw new InvalidOperationException()
                    };
                    foreach (var child in permNode.ExpressionNode!.Children)
                    {
                        walkExpression(entityType, child, lookupOperationNode);
                    }
                    lookupNode.Children.Add(new LookupNode(lookupOperationNode));
                    break;
                case PermissionNodeType.Leaf:
                    if (permNode.LeafNode!.Value.Split('.') is [{ } relation, { } entityPermission])
                    {
                        var r = schema.GetRelations(entityType)
                            .First(x => x.Name.Equals(relation, StringComparison.InvariantCultureIgnoreCase));

                        foreach (var entity in r.Entities)
                        {
                            var joinNode = new LookupExpressionNode(LookupNodeExpressionType.Join, []);
                            var childRelation = new Relation
                            {
                                Name = relation,
                                EntityType = entityType,
                                SubjectType = entity.Type,
                                SubjectRelation = entity.Relation
                            };
                            var childRelationUnion = new RelationOrAttribute(childRelation);
                            
                            joinNode.Children.Add(LookupNode.Leaf(childRelationUnion));
                            relationSet.Add(childRelationUnion);
                            
                            walk(entity.Type, entity.Relation ?? entityPermission, joinNode);
                            
                            lookupNode.Children.Add(new LookupNode(joinNode));
                        }
                    }
                    else
                    {
                        walk(entityType, permNode.LeafNode.Value, lookupNode);
                    }

                    break;
            }
        }

        void walk(string entityType, string entityPermission, LookupExpressionNode node)
        {
            var permission = schema.GetPermissions(entityType)
                .FirstOrDefault(x => x.Name.Equals(entityPermission, StringComparison.InvariantCultureIgnoreCase));

            var relation = schema.GetRelations(entityType)
                .FirstOrDefault(x => x.Name.Equals(entityPermission, StringComparison.InvariantCultureIgnoreCase));

            var attribute = schema.GetAttributes(entityType)
                .FirstOrDefault(x => x.Name.Equals(entityPermission, StringComparison.InvariantCultureIgnoreCase));

            var type = new { permission, relation, attribute } switch
            {
                { permission: null, relation: not null } => RelationType.DirectRelation,
                { permission: not null, relation: null } => RelationType.Permission,
                { attribute: not null } => RelationType.Attribute,
                _ => RelationType.None
            };

            switch (type)
            {
                case RelationType.DirectRelation:
                    var (_, expandedRelations) = ExpandRelation(entityType, relation!, finalSubjectType, node);
                    foreach (var r in expandedRelations)
                    {
                        relationSet.Add(r);
                    }
                    break;

                case RelationType.Attribute:
                    var attr = new RelationOrAttribute(new Attribute
                    {
                        EntityType = entityType,
                        Name = entityPermission
                    });
                    node.Children.Add(LookupNode.Leaf(attr));
                    relationSet.Add(attr);
                    break;

                case RelationType.Permission:
                    walkExpression(entityType, permission!.Tree, node);
                    break;
            }
        }

        walk(rootEntityType, rootEntityPermission, rootExpression);

        if (rootExpression.Children.Count == 1)
        {
            return (rootExpression.Children[0], relationSet);
        }

        return (new LookupNode(rootExpression), relationSet);
    }

    private (LookupNode, HashSet<RelationOrAttribute>) ExpandRelation(string rootEntity, Schemas.Relation rootRelation,
        string subjectType, LookupExpressionNode? parentNode = null)
    {
        var rootExpression = parentNode ?? new LookupExpressionNode(LookupNodeExpressionType.Union, []);
        var relations = new HashSet<RelationOrAttribute>();

        void walk(string entity, Schemas.Relation relation, LookupExpressionNode node)
        {
            var unionNode = new LookupExpressionNode(LookupNodeExpressionType.Union, []);

            foreach (var relationEntity in relation.Entities)
            {
                relations.Add(new RelationOrAttribute(new Relation
                {
                    Name = relation.Name,
                    EntityType = entity,
                    SubjectType = relationEntity.Type,
                    SubjectRelation = relationEntity.Relation
                }));

                if (relationEntity.Type == subjectType)
                {
                    unionNode.Children.Add(LookupNode.Leaf(new Relation
                    {
                        Name = relation.Name,
                        EntityType = entity,
                        SubjectType = relationEntity.Type,
                        SubjectRelation = relationEntity.Relation
                    }));
                    continue;
                }

                var subRelation = schema.GetRelations(relationEntity.Type)
                    .First(x => x.Name == relationEntity.Relation);

                if (relationEntity.Relation is not null)
                {
                    var joinNode = new LookupExpressionNode(LookupNodeExpressionType.Join, []);
                    joinNode.Children.Add(LookupNode.Leaf(new Relation
                    {
                        Name = relation.Name,
                        EntityType = entity,
                        SubjectType = relationEntity.Type,
                        SubjectRelation = relationEntity.Relation
                    }));
                    walk(relationEntity.Type, subRelation, joinNode);

                    unionNode.Children.Add(new LookupNode(joinNode));
                }
                else
                {
                    unionNode.Children.Add(LookupNode.Leaf(new Relation
                    {
                        Name = relation.Name,
                        EntityType = entity,
                        SubjectType = relationEntity.Type,
                        SubjectRelation = relationEntity.Relation
                    }));
                    walk(relationEntity.Type, subRelation, unionNode);
                }
            }

            if (unionNode.Children.Count == 1)
            {
                node.Children.Add(unionNode.Children[0]);
                return;
            }

            node.Children.Add(new LookupNode(unionNode));
        }

        walk(rootEntity, rootRelation, rootExpression);

        if (rootExpression.Children.Count == 1)
        {
            return (rootExpression.Children[0], relations);
        }

        return (new LookupNode(rootExpression), relations);
    }
}

public record RelationOrAttributeTuple
{
    public RelationOrAttributeTuple(RelationTuple relationTuple)
    {
        RelationTuple = relationTuple;
        Type = RelationOrAttributeType.Relation;
    }

    public RelationOrAttributeTuple(AttributeTuple attributeTuple)
    {
        AttributeTuple = attributeTuple;
        Type = RelationOrAttributeType.Attribute;
    }

    public AttributeTuple? AttributeTuple { get; init; }
    public RelationTuple? RelationTuple { get; init; }
    public RelationOrAttributeType Type { get; init; }

    public string EntityId => Type == RelationOrAttributeType.Relation
        ? RelationTuple!.EntityId
        : AttributeTuple!.EntityId;

    public string EntityType => Type == RelationOrAttributeType.Relation
        ? RelationTuple!.EntityType
        : AttributeTuple!.EntityType;
}

public enum LookupNodeState
{
    Loaded,
    NotLoaded,
    Defer
}

public enum RelationOrAttributeType
{
    Attribute,
    Relation
}

public record Relation
{
    public string? SubjectRelation { get; init; }
    public required string SubjectType { get; init; }
    public required string EntityType { get; init; }
    public required string Name { get; init; }
}

public record Attribute
{
    public required string Name { get; init; }
    public required string EntityType { get; init; }
}

public record RelationOrAttribute
{
    public RelationOrAttribute(Relation relation)
    {
        Relation = relation;
        Type = RelationOrAttributeType.Relation;
    }

    public RelationOrAttribute(Attribute attribute)
    {
        Attribute = attribute;
        Type = RelationOrAttributeType.Attribute;
    }

    public RelationOrAttributeType Type { get; init; }
    public Attribute? Attribute { get; init; }
    public Relation? Relation { get; init; }
}

public enum LookupNodeType
{
    Leaf,
    Expression
}

public enum LookupNodeExpressionType
{
    Union,
    Intersect,
    Join
}

public record LookupNode
{
    public LookupNodeLeaf? LeafNode { get; init; }
    public LookupExpressionNode? ExpressionNode { get; init; }
    public LookupNodeType Type { get; init; }

    public LookupNode(LookupExpressionNode expressionNode)
    {
        ExpressionNode = expressionNode;
        Type = LookupNodeType.Expression;
    }

    public LookupNode(LookupNodeLeaf leafNode)
    {
        LeafNode = leafNode;
        Type = LookupNodeType.Leaf;
    }

    public static LookupNode Leaf(Relation value)
    {
        return new LookupNode(new LookupNodeLeaf(new RelationOrAttribute(value)));
    }

    public static LookupNode Leaf(Attribute value)
    {
        return new LookupNode(new LookupNodeLeaf(new RelationOrAttribute(value)));
    }

    public static LookupNode Leaf(RelationOrAttribute value)
    {
        return new LookupNode(new LookupNodeLeaf(value));
    }
}

public record LookupExpressionNode(LookupNodeExpressionType Operation, List<LookupNode> Children);

public record LookupNodeLeaf(RelationOrAttribute Value);