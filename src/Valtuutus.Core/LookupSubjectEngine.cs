using System.Diagnostics;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Schemas;
using LookupSubjectFunction =
    System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<Valtuutus.Core.RelationOrAttributeTuples>>;

namespace Valtuutus.Core;

internal record LookupSubjectRequestInternal
{
    public required string EntityType { get; init; }
    public required IList<string> EntitiesIds { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public string? SubjectRelation { get; init; }
    public required string FinalSubjectType { get; init; }
    public required string RootEntityType { get; init; }
    public required string RootEntityId { get; init; }
    public required int Depth { get; set; } = 10;

    public bool CheckDepth() =>
        Depth == 0;

    public void DecreaseDepth() =>
        Depth--;
}

public sealed class LookupSubjectEngine(
    Schema schema,
    IDataReaderProvider reader)
{
    /// <summary>
    /// The LookupSubject lets you ask "Which subjects of type T can do action Y on entity:X?"
    /// </summary>
    /// <param name="req">The object containing information for which </param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The list of ids of subjects of the provided type that has the permission on the specified entity.</returns>
    public async Task<HashSet<string>> Lookup(LookupSubjectRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateLookupSubjectSpanAttributes(req));
        var internalReq = new LookupSubjectRequestInternal
        {
            Permission = req.Permission,
            EntityType = req.EntityType,
            SubjectType = req.SubjectType,
            EntitiesIds = [req.EntityId],
            FinalSubjectType = req.SubjectType,
            RootEntityId = req.EntityId,
            RootEntityType = req.EntityType,
            Depth = req.Depth
        };

        var res = await LookupInternal(internalReq)(ct);
        var hs = new HashSet<string>(res.RelationsTuples!.Select(x => x.SubjectId).OrderBy(x => x));

        activity?.AddEvent(new ActivityEvent("LookupSubjectResult", tags: new ActivityTagsCollection(CreateLookupSubjectResultAttributes(hs))));
        return hs;
    }
    
        
    private static IEnumerable<KeyValuePair<string, object?>> CreateLookupSubjectResultAttributes(HashSet<string> result)
    {
        yield return new KeyValuePair<string, object?>("LookupSubjectResultCount", result.Count);
    }
    
    private static IEnumerable<KeyValuePair<string, object?>> CreateLookupSubjectSpanAttributes(LookupSubjectRequest req)
    {
        yield return new KeyValuePair<string, object?>("LookupSubjectRequest", req);
    }

    private static LookupSubjectFunction Fail()
    {
        return (_) => Task.FromResult(new RelationOrAttributeTuples(Enumerable.Empty<RelationTuple>().ToList()));
    }

    private LookupSubjectFunction LookupInternal(LookupSubjectRequestInternal req)
    {
        if (req.CheckDepth())
            return Fail();

        req.DecreaseDepth();

        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return schema.GetRelationType(req.EntityType, req.Permission) switch
        {
            RelationType.DirectRelation => LookupRelation(req, schema.GetRelation(req.EntityType, req.Permission)),
            RelationType.Permission => LookupPermission(req, schema.GetPermission(req.EntityType, req.Permission)),
            RelationType.Attribute => LookupAttribute(req, schema.GetAttribute(req.EntityType, req.Permission)),
            _ => throw new InvalidOperationException()
        };

    }

    private LookupSubjectFunction LookupPermission(LookupSubjectRequestInternal req, Permission permission)
    {
        var permNode = permission.Tree;
    
        return permNode.Type == PermissionNodeType.Expression
            ? LookupExpression(req, permNode.ExpressionNode!)
            : LookupLeaf(req, permNode.LeafNode!);
    }

    private LookupSubjectFunction LookupExpression(LookupSubjectRequestInternal req, PermissionNodeOperation node)
    {
        return node.Operation switch
        {
            PermissionOperation.Intersect => LookupExpressionChildren(req, node.Children, IntersectEntities),
            PermissionOperation.Union => LookupExpressionChildren(req, node.Children, UnionEntities),
            _ => throw new InvalidOperationException()
        };
    }

    private LookupSubjectFunction LookupExpressionChildren(LookupSubjectRequestInternal req, List<PermissionNode> children,
        Func<List<LookupSubjectFunction>, CancellationToken, Task<RelationOrAttributeTuples>> aggregator)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
    
        var lookupFunctions = new List<LookupSubjectFunction>(capacity: children.Count);
        foreach (var child in children)
        {
            switch (child.Type)
            {
                case PermissionNodeType.Expression:
                    lookupFunctions.Add(LookupExpression(req, child.ExpressionNode!));
                    break;
                case PermissionNodeType.Leaf:
                    lookupFunctions.Add(LookupLeaf(req, child.LeafNode!));
                    break;
            }
        }
    
        return async (ct) => await aggregator(lookupFunctions, ct);
    }

    private LookupSubjectFunction LookupLeaf(LookupSubjectRequestInternal req, PermissionNodeLeaf node)
    {
        return node.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, node.PermissionNode!),
            PermissionNodeLeafType.AttributeExpression => CheckLeafAttributeExp(req, node.ExpressionNode!),
            _ => throw new InvalidOperationException()
        };
    }
    
    private LookupSubjectFunction CheckLeafPermission(LookupSubjectRequestInternal req, PermissionNodeLeafPermission node)
    {
        var perm = node.Permission;

        if (perm.Split('.') is [{ } userSet, { } computedUserSet])
        {
            // Indirect Relation
            return CheckTupleToUserSet(req, userSet, computedUserSet);
        }

        // Direct Relation
        return LookupComputedUserSet(req, perm);
    }
    
    private LookupSubjectFunction CheckLeafAttributeExp(LookupSubjectRequestInternal req, PermissionNodeLeafAttributeExp node)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            var attrName = node.AttributeName;

            var res = (await reader.GetAttributes(new AttributeFilter
                {
                    Attribute = attrName,
                    EntityType = req.EntityType
                }, req.EntitiesIds, ct))
                .Where(AttrEvaluator)
                .ToList();

            return new RelationOrAttributeTuples(res);

            bool AttrEvaluator(AttributeTuple attrTuple)
            {
                return node.Type switch
                {
                    AttributeTypes.Decimal => node.DecimalExpression!(attrTuple.Value.GetValue<decimal>()),
                    AttributeTypes.Int => node.IntExpression!(attrTuple.Value.GetValue<int>()),
                    AttributeTypes.String => node.StringExpression!(attrTuple.Value.GetValue<string>()),
                    _ => throw new InvalidOperationException()
                };
            }
        };
    }

    private LookupSubjectFunction CheckTupleToUserSet(LookupSubjectRequestInternal req, string tupleSetRelation,
        string computedUserSetRelation)
    {
        return async (ct) =>
        {
            var relation = schema.GetRelation(req.EntityType, tupleSetRelation);
    
            var lookupFunctions = new List<LookupSubjectFunction>(capacity: relation.Entities.Count);
    
            foreach (var entity in relation.Entities)
            {
                var relations = await reader.GetRelationsWithEntityIds(
                    new EntityRelationFilter
                    {
                        Relation = relation.Name,
                        EntityType = req.EntityType
                    },
                    entity.Type,
                    req.EntitiesIds,
                    entity.Relation,
                    ct
                );
                    
                lookupFunctions.Add(LookupInternal(req with
                {
                    EntityType = entity.Type,
                    Permission = computedUserSetRelation,
                    EntitiesIds = relations.Select(x => x.SubjectId).ToArray()
                }));
            }
    
            return await UnionEntities(lookupFunctions, ct);
        };
    }
    
    private LookupSubjectFunction LookupComputedUserSet(LookupSubjectRequestInternal req, string computedUserSetRelation)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
    
        return LookupInternal(req with
        {
            Permission = computedUserSetRelation
        });
    }

    private LookupSubjectFunction LookupAttribute(LookupSubjectRequestInternal req, Schemas.Attribute attribute)
    {
        return async (ct) =>
        {
            var res = (await reader.GetAttributes(new AttributeFilter
            {
                Attribute = attribute.Name,
                EntityType = req.EntityType
            }, req.EntitiesIds, ct))
            .Where(x => x.Value.TryGetValue<bool>(out var b) && b)
            .ToList();
            
            return new RelationOrAttributeTuples(res);
        };
    }

    private LookupSubjectFunction LookupRelation(LookupSubjectRequestInternal req, Relation relation)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

            var lookupFunctions = new List<LookupSubjectFunction>(capacity: relation.Entities.Count);

            foreach (var relationEntity in relation.Entities)
            {
                if (relationEntity.Type == req.FinalSubjectType)
                {
                    lookupFunctions.Add(LookupRelationLeaf(req with
                    {
                        SubjectType = relationEntity.Type,
                    }));
                    continue;
                }

                var subRelation = relationEntity.Relation is null ? null : schema.GetRelation(relationEntity.Type, relationEntity.Relation);

                if (subRelation is not null)
                {
                    var relations = await reader.GetRelationsWithEntityIds(
                        new EntityRelationFilter
                        {
                            Relation = req.Permission,
                            EntityType = req.EntityType
                        },
                        relationEntity.Type,
                        req.EntitiesIds,
                        subRelation.Name,
                        ct
                    );
                    
                    lookupFunctions.Add(LookupRelation(req with
                    {
                        EntityType = relationEntity.Type,
                        Permission = relationEntity.Relation!,
                        EntitiesIds = relations.Select(x => x.SubjectId).ToArray()
                    }, subRelation));
                }
            }

            return await UnionEntities(lookupFunctions, ct);
        };
    }

    private LookupSubjectFunction LookupRelationLeaf(LookupSubjectRequestInternal req)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            var res = await reader.GetRelationsWithEntityIds(
                new EntityRelationFilter
                {
                    Relation = req.Permission,
                    EntityType = req.EntityType
                },
                req.SubjectType,
                req.EntitiesIds,
                req.SubjectRelation,
                ct
            );

            return new RelationOrAttributeTuples(res);
        };
    }


    private async Task<RelationOrAttributeTuples> UnionEntities(List<LookupSubjectFunction> functions,
        CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var relations = (await Task.WhenAll(functions.Select(f => f(ct))))
            .SelectMany(x => x.Type == RelationOrAttributeType.Relation ? x.RelationsTuples! : [])
            .ToList();

        return new RelationOrAttributeTuples(relations);
    }

    private async Task<RelationOrAttributeTuples> IntersectEntities(List<LookupSubjectFunction> functions,
        CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var overlappingItems = Enumerable.Empty<RelationTuple>();
        var results = await Task.WhenAll(functions.Select(f => f(ct)));

        // If the intersection is between 2 relations, check if the subject is present in the 2 relations
        
        // If the intersection is between a relation and an attribute, we need to check if the attribute is present and is `true`
        // then we return the relations
        
        var count = 0;
        foreach (var result in results)
        {
            if (result.Type == RelationOrAttributeType.Attribute)
            {
                if (result.AttributesTuples!.Count != 0)
                {
                    continue;
                }

                return new RelationOrAttributeTuples(new List<RelationTuple>());
            }

            var relations = result.RelationsTuples!;
            
            overlappingItems = count >= 1
                ? overlappingItems
                    .Select(x => x)
                    .Intersect(
                    relations,
                    RelationTupleComparer.Instance)
                : relations;

            count++;
        }

        var res = overlappingItems.ToList();
        return new RelationOrAttributeTuples(res);
    }
}

internal record RelationOrAttributeTuples
{
    public RelationOrAttributeTuples(List<RelationTuple> relationsTuples)
    {
        RelationsTuples = relationsTuples;
        Type = RelationOrAttributeType.Relation;
    }

    public RelationOrAttributeTuples(List<AttributeTuple> attributesTuples)
    {
        AttributesTuples = attributesTuples;
        Type = RelationOrAttributeType.Attribute;
    }

    public List<AttributeTuple>? AttributesTuples { get; init; }
    public List<RelationTuple>? RelationsTuples { get; init; }
    public RelationOrAttributeType Type { get; init; }
}