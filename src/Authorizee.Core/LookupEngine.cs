using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Microsoft.Extensions.Logging;

namespace Authorizee.Core;

public class LookupEngine(
    Schema schema,
    PermissionEngine permissionEngine,
    ILogger<LookupEngine> logger,
    IRelationTupleReader tupleReader)
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

        var entitiesIds = await (type switch
        {
            RelationType.DirectRelation => LookupRelation(req, relation!, ct),
            RelationType.Permission => LookupPermission(req, permission!, ct),
            _ => throw new InvalidOperationException()
        });

        return new ConcurrentBag<string>(entitiesIds);

        // var relationsOrAttributes =
        //     GetPermissionRelationsAndAttributes(req.EntityType, req.Permission, req.SubjectType);
        //
        // var relations = relationsOrAttributes.Where(x => x.Type == RelationOrAttributeType.Relation)
        //     .Select(x => x.Relation!);
        //
        // var requestedSubjectRelations = relations.Where(x =>
        //     x.SubjectType.Equals(req.SubjectType, StringComparison.InvariantCultureIgnoreCase));
        //
        // var relationTuples =
        //     await tupleReader.GetRelations(requestedSubjectRelations
        //             .Select(x => new EntityRelationFilter
        //             {
        //                 Relation = x.Name,
        //                 EntityType = x.EntityType
        //             }),
        //         new SubjectFilter
        //         {
        //             SubjectId = req.SubjectId,
        //             SubjectType = req.SubjectType
        //         }
        //     );


        ConcurrentBag<string> entityIDs = [];

        var callback = (string entityId) => { entityIDs.Add(entityId); };

        var bulkChecker = new ActionBlock<CheckRequest>(async request =>
        {
            var result = await permissionEngine.Check(request, ct);
            if (result)
            {
                callback(request.EntityId);
            }
        }, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 5
        });

        bulkChecker.Complete();
        await bulkChecker.Completion;
        return entityIDs;
    }

    private async Task<IList<string>> LookupRelation(LookupEntityRequest req, Schemas.Relation relation,
        CancellationToken ct)
    {
        var (relationTree, relations) = ExpandRelation(req.EntityType, relation, req.SubjectType);

        // var loadedRelationsOrAttributes = new Dictionary<RelationOrAttribute, LookupNodeState>();
        var loadedRelations = new Dictionary<Relation, LookupNodeState>();
        var relationsValues = new Dictionary<Relation, RelationTuple[]>();
        var attributeValues = new Dictionary<Attribute, AttributeTuple[]>();

        var requestedSubjectRelations = relations.Where(x =>
                x.Type == RelationOrAttributeType.Relation
                && x.Relation!.SubjectType == req.SubjectType)
            .Select(x => x.Relation!)
            .ToArray();

        var z = requestedSubjectRelations
            .Select(x => new EntityRelationFilter
            {
                Relation = x.Name,
                EntityType = x.EntityType
            })
            .ToArray();
        
        var relationTuplesWithSubject =
            await tupleReader.GetRelations(z,
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
                .ToArray();

            relationsValues.Add(r, relationInstanceTuples);
        }

        async Task<IList<RelationTuple>> walk(LookupNode node)
        {
            async Task<IList<RelationTuple>> handleLeafNode()
            {
                // TODO: Handle attributes
                var nodeRelation = node.LeafNode!.Value.Relation!;
                if (loadedRelations.TryGetValue(nodeRelation, out LookupNodeState value) && value == LookupNodeState.Loaded)
                {
                    return relationsValues[nodeRelation];
                }

                var relatedRelation = relations
                    .Where(x =>
                        x.Relation!.EntityType == nodeRelation.SubjectType &&
                        x.Relation!.Name == nodeRelation.SubjectRelation)
                    .Select(x => x.Relation!)
                    .FirstOrDefault();

                var subjectValues = relatedRelation is null
                    ? []
                    : relationsValues[relatedRelation];

                return await tupleReader.GetRelations(
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
                );
            }

            async Task<IList<RelationTuple>> handleExpressionNode()
            {
                if (node.ExpressionNode!.Operation == LookupNodeExpressionType.Union)
                {
                    var entities = new List<RelationTuple>();
                    foreach (var child in node.ExpressionNode!.Children)
                    {
                        entities.AddRange(await walk(child));
                    }

                    return entities.ToArray();
                }
                
                if (node.ExpressionNode!.Operation == LookupNodeExpressionType.Join &&
                    node.ExpressionNode.Children is [{ } childA, { } childB])
                {
                    var childAEntities = await walk(childA);
                    var childBEntities = await walk(childB);

                    return childAEntities.Join(
                        childBEntities,
                        a => new { Type = a.SubjectType, Id = a.SubjectId },
                        b => new { Type = b.EntityType, Id = b.EntityId },
                        (a, b) => a.EntityType == req.EntityType
                            ? a
                            : b)
                        .ToArray();
                }
                
                return [];
            }
            
            var actionMapper = new Dictionary<LookupNodeType, Func<Task<IList<RelationTuple>>>>()
            {
                { LookupNodeType.Expression, handleExpressionNode },
                { LookupNodeType.Leaf, handleLeafNode }
            };

            return await actionMapper[node.Type]();
        }

        return (await walk(relationTree))
            .Select(x => x.EntityId)
            .ToArray();

        return [];
        // var requestedSubjectRelations = relations.Where(x =>
        //     x.SubjectType.Equals(req.SubjectType, StringComparison.InvariantCultureIgnoreCase));
        // var remainingRelations = relations.Where(x =>
        //     !x.SubjectType.Equals(req.SubjectType, StringComparison.InvariantCultureIgnoreCase));
        //
        // var relationTuplesWithSubject =
        //     await tupleReader.GetRelations(requestedSubjectRelations
        //             .Select(x => new EntityRelationFilter
        //             {
        //                 Relation = x.Name,
        //                 EntityType = x.EntityType
        //             }),
        //         new SubjectFilter
        //         {
        //             SubjectId = req.SubjectId,
        //             SubjectType = req.SubjectType
        //         }
        //     );
        //
        // var remainingRelationTuples =
        //     await tupleReader.GetRelations(remainingRelations
        //             .Select(x => new EntityRelationFilter
        //             {
        //                 Relation = x.Name,
        //                 EntityType = x.EntityType
        //             }),
        //         null
        //     );
        //
        // var entities = new ConcurrentBag<string>();
        //
        // void walk(Schemas.Relation rel)
        // {
        //     foreach (var relationEntity in rel.Entities)
        //     {
        //         if (relationEntity.Type == req.SubjectType)
        //         {
        //             var relationEntities = relationTuplesWithSubject
        //                 .Where(x => x.EntityType == req.EntityType
        //                             && x.Relation == relation.Name);
        //         
        //             foreach (var e in relationEntities)
        //             {
        //                 entities.Add(e.EntityId);
        //             }
        //
        //             continue;
        //         }
        //         
        //         var subRelation = schema.GetRelations(relationEntity.Type)
        //             .First(x => x.Name == relationEntity.Relation);
        //
        //         expand(relationEntity.Type, subRelation);
        //     }
        // }
        //
        // walk(relation);
        //
        // return entities.ToList();
    }

    private Task<IList<string>> LookupPermission(LookupEntityRequest req, Schemas.Permission permission,
        CancellationToken ct)
    {
        throw new NotImplementedException();
        // return Task.FromResult(Array.Empty<string>());
    }

    private HashSet<RelationOrAttribute> GetPermissionRelationsAndAttributes(string finalEntityType,
        string finalEntityPermission, string finalSubjectType)
    {
        var relationSet = new HashSet<RelationOrAttribute>();

        void walkLeaf(string entityType, string entityPermission)
        {
            var relation = schema.GetRelations(entityType)
                .First(x => x.Name.Equals(entityPermission, StringComparison.InvariantCultureIgnoreCase));

            foreach (var entity in relation.Entities)
            {
                relationSet.Add(new RelationOrAttribute(new Relation
                {
                    Name = entityPermission,
                    EntityType = entityType,
                    SubjectType = entity.Type
                }));

                if (entity.Type == finalSubjectType)
                {
                    continue;
                }

                if (entity.Relation is not null)
                    walk(entity.Type, entity.Relation);
            }
        }

        void walkExpression(string entityType, PermissionNode node)
        {
            switch (node.Type)
            {
                case PermissionNodeType.Expression:
                    foreach (var child in node.ExpressionNode!.Children)
                    {
                        walkExpression(entityType, child);
                    }

                    break;
                case PermissionNodeType.Leaf:
                    if (node.LeafNode!.Value.Split('.') is [{ } relation, { } entityPermission])
                    {
                        var r = schema.GetRelations(entityType)
                            .First(x => x.Name.Equals(relation, StringComparison.InvariantCultureIgnoreCase));

                        foreach (var entity in r.Entities)
                        {
                            walk(entity.Type, entity.Relation ?? entityPermission);
                        }
                    }
                    else
                    {
                        walk(entityType, node.LeafNode.Value);
                    }

                    break;
            }
        }

        void walk(string entityType, string entityPermission)
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
                    walkLeaf(entityType, entityPermission);
                    break;

                case RelationType.Attribute:
                    relationSet.Add(new RelationOrAttribute(new Attribute
                    {
                        EntityType = entityType,
                        Name = entityPermission
                    }));
                    break;

                case RelationType.Permission:
                    walkExpression(entityType, permission.Tree);
                    break;
            }
        }

        walk(finalEntityType, finalEntityPermission);

        return relationSet;
    }

    private (LookupNode, HashSet<RelationOrAttribute>) ExpandRelation(string rootEntity, Schemas.Relation rootRelation,
        string subjectType)
    {
        var rootExpression = new LookupExpressionNode(LookupNodeExpressionType.Union, []);
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
}

public record LookupExpressionNode(LookupNodeExpressionType Operation, List<LookupNode> Children);

public record LookupNodeLeaf(RelationOrAttribute Value);