using System.Collections.Concurrent;
using Authorizee.Core.Data;
using Authorizee.Core.Observability;
using Authorizee.Core.Schemas;
using Microsoft.Extensions.Logging;
using LookupSubjectFunction =
    System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<Authorizee.Core.RelationOrAttributeTuples>>;

namespace Authorizee.Core;

public record LookupSubjectRequestInternal
{
    public required string EntityType { get; init; }
    public required IList<string> EntitiesIds { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public string? SubjectRelation { get; init; }
    public required string FinalSubjectType { get; init; }
    public required string RootEntityType { get; init; }
    public required string RootEntityId { get; init; }
}

public class LookupSubjectEngine(
    Schema schema,
    IRelationTupleReader tupleReader,
    IAttributeReader attributeReader)
{
    public async Task<ConcurrentBag<string>> Lookup(LookupSubjectRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        var internalReq = new LookupSubjectRequestInternal
        {
            Permission = req.Permission,
            EntityType = req.EntityType,
            SubjectType = req.SubjectType,
            EntitiesIds = [req.EntityId],
            FinalSubjectType = req.SubjectType,
            RootEntityId = req.EntityId,
            RootEntityType = req.EntityType
        };

        var res = await LookupInternal(internalReq)(ct);
        return new ConcurrentBag<string>(res.RelationsTuples!.Select(x => x.SubjectId).Distinct().OrderBy(x => x));
    }

    private LookupSubjectFunction LookupInternal(LookupSubjectRequestInternal req)
    {
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
        var perm = node.Value;
    
        if (perm.Split('.') is [{ } userSet, { } computedUserSet])
        {
            // Indirect Relation
            return CheckTupleToUserSet(req, userSet, computedUserSet);
        }
    
        // Direct Relation
        return LookupComputedUserSet(req, perm);
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
                var relations = await tupleReader.GetRelations(
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
            var res = await attributeReader.GetAttributes(new AttributeFilter
            {
                Attribute = attribute.Name,
                EntityType = req.EntityType
            }, req.EntitiesIds, ct);
            
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
                    var relations = await tupleReader.GetRelations(
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
            var res = await tupleReader.GetRelations(
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
                if (result.AttributesTuples!.Exists(x => x.Value.TryGetValue<bool>(out var b) && b))
                {
                    continue;
                }

                return new RelationOrAttributeTuples(new List<RelationTuple>());
            }

            var relations = result.RelationsTuples!;
            
            overlappingItems = count >= 1
                ? overlappingItems
                    .Select(x => x)
                    .IntersectBy(
                    relations.Select(x => new { Type = x.SubjectType, Id = x.SubjectId }),
                    x => new { Type = x.SubjectType, Id = x.SubjectId })
                : relations;

            count++;
        }

        var res = overlappingItems.ToList();
        return new RelationOrAttributeTuples(res);
    }
}

public record RelationOrAttributeTuples
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