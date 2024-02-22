using System.Collections.Concurrent;
using Authorizee.Core.Data;
using Authorizee.Core.Observability;
using Authorizee.Core.Schemas;
using Microsoft.Extensions.Logging;
using LookupFunction =
    System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<
        System.Collections.Generic.List<Authorizee.Core.RelationOrAttributeTuple>>>;

namespace Authorizee.Core;

public record LookupEntityRequestInternal
{
    public required string EntityType { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public required IList<string> SubjectsIds { get; init; }
    public string? SubjectRelation { get; init; }
    public required string FinalSubjectType { get; init; }
    public required string FinalSubjectId { get; init; }
}

public class LookupEngine(
    Schema schema,
    ILogger<LookupEngine> logger,
    IRelationTupleReader tupleReader,
    IAttributeReader attributeReader)
{
    public async Task<ConcurrentBag<string>> LookupEntity(LookupEntityRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        var internalReq = new LookupEntityRequestInternal
        {
            Permission = req.Permission,
            EntityType = req.EntityType,
            SubjectType = req.SubjectType,
            SubjectsIds = [req.SubjectId],
            FinalSubjectType = req.SubjectType,
            FinalSubjectId = req.SubjectId
        };

        var res = await LookupEntityInternal(internalReq)(ct);
        return new ConcurrentBag<string>(res.Select(x => x.EntityId).Distinct().OrderBy(x => x));
    }

    private LookupFunction LookupEntityInternal(LookupEntityRequestInternal req)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        var permission = schema.GetPermissions(req.EntityType)
            .FirstOrDefault(x => x.Name.Equals(req.Permission, StringComparison.InvariantCultureIgnoreCase));

        var relation = schema.GetRelations(req.EntityType)
            .FirstOrDefault(x => x.Name.Equals(req.Permission, StringComparison.InvariantCultureIgnoreCase));

        var attribute = schema.GetAttributes(req.EntityType)
            .FirstOrDefault(x => x.Name.Equals(req.Permission, StringComparison.InvariantCultureIgnoreCase));

        var type = new { permission, relation, attribute } switch
        {
            { permission: null, relation: not null } => RelationType.DirectRelation,
            { permission: not null, relation: null } => RelationType.Permission,
            { permission: null, relation: null, attribute: not null } => RelationType.Attribute,
            _ => RelationType.None
        };

        return type switch
        {
            RelationType.DirectRelation => LookupRelation(req, relation!),
            RelationType.Permission => LookupPermission(req, permission!),
            RelationType.Attribute => LookupAttribute(req, attribute!)
        };
    }

    private LookupFunction LookupPermission(LookupEntityRequestInternal req, Permission permission)
    {
        var permNode = permission.Tree;

        return permNode.Type == PermissionNodeType.Expression
            ? LookupExpression(req, permNode.ExpressionNode!)
            : LookupLeaf(req, permNode.LeafNode!);
    }

    private LookupFunction LookupExpression(LookupEntityRequestInternal req, PermissionNodeOperation node)
    {
        return node.Operation switch
        {
            PermissionOperation.Intersect => LookupExpressionChildren(req, node.Children, IntersectEntities),
            PermissionOperation.Union => LookupExpressionChildren(req, node.Children, UnionEntities),
            _ => throw new InvalidOperationException()
        };
    }

    private LookupFunction LookupExpressionChildren(LookupEntityRequestInternal req, List<PermissionNode> children,
        Func<List<LookupFunction>, CancellationToken, Task<List<RelationOrAttributeTuple>>> aggregator)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var lookupFunctions = new List<LookupFunction>(capacity: children.Count);
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

    private LookupFunction LookupLeaf(LookupEntityRequestInternal req, PermissionNodeLeaf node)
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

    private LookupFunction CheckTupleToUserSet(LookupEntityRequestInternal req, string tupleSetRelation,
        string computedUserSetRelation)
    {
        return async (ct) =>
        {
            var relation = schema.GetRelations(req.EntityType)
                .First(x => x.Name.Equals(tupleSetRelation, StringComparison.InvariantCultureIgnoreCase));

            var lookupFunctions = new List<LookupFunction>(capacity: relation.Entities.Count);

            foreach (var entity in relation.Entities)
            {
                var main = (List<RelationOrAttributeTuple> relatedTuples) =>
                {
                    using var activityMain = DefaultActivitySource.InternalSourceInstance.StartActivity("join main FN");
                    if (relatedTuples.Count > 0)
                    {
                        return LookupRelationLeaf(req with
                        {
                            Permission = tupleSetRelation,
                            EntityType = req.EntityType,
                            SubjectType = entity.Type,
                            SubjectsIds = relatedTuples.Select(x => x.EntityId).ToList(),
                            SubjectRelation = entity.Relation
                        });
                    }

                    return (_) => Task.FromResult<List<RelationOrAttributeTuple>>([]);
                };

                var dependent = LookupEntityInternal(req with
                {
                    EntityType = entity.Type,
                    Permission = computedUserSetRelation,
                });

                lookupFunctions.Add((ct) => JoinEntities(main, dependent, ct));
            }

            return await UnionEntities(lookupFunctions, ct);
        };
    }

    private LookupFunction LookupComputedUserSet(LookupEntityRequestInternal req, string computedUserSetRelation)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return LookupEntityInternal(req with
        {
            Permission = computedUserSetRelation
        });
    }

    private LookupFunction LookupAttribute(LookupEntityRequestInternal req, Schemas.Attribute attribute)
    {
        return async (ct) =>
        {
            return (await attributeReader.GetAttributes(new AttributeFilter
                {
                    Attribute = attribute.Name,
                    EntityType = req.EntityType
                }))
                .Where(a => a.Value.TryGetValue(out bool b) && b)
                .Select(x => new RelationOrAttributeTuple(x))
                .ToList();
        };
    }

    private LookupFunction LookupRelation(LookupEntityRequestInternal req, Schemas.Relation relation)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

            var lookupFunctions = new List<LookupFunction>(capacity: relation.Entities.Count);

            foreach (var relationEntity in relation.Entities)
            {
                if (relationEntity.Type == req.FinalSubjectType)
                {
                    lookupFunctions.Add(LookupRelationLeaf(req with
                    {
                        SubjectType = relationEntity.Type,
                        SubjectsIds = [req.FinalSubjectId]
                    }));
                    continue;
                }

                var subRelation = schema.GetRelations(relationEntity.Type)
                    .FirstOrDefault(x => x.Name == relationEntity.Relation);

                if (subRelation is not null)
                {
                    var main = (List<RelationOrAttributeTuple> relatedTuples) =>
                    {
                        using var activityMain =
                            DefaultActivitySource.InternalSourceInstance.StartActivity("join main FN");
                        if (relatedTuples.Count > 0)
                        {
                            return LookupRelationLeaf(req with
                            {
                                Permission = relation.Name,
                                EntityType = req.EntityType,
                                SubjectType = relationEntity.Type,
                                SubjectsIds = relatedTuples.Select(x => x.EntityId).ToList(),
                                SubjectRelation = relationEntity.Relation
                            });
                        }

                        return (_) => Task.FromResult<List<RelationOrAttributeTuple>>([]);
                    };

                    var dependent = LookupRelation(req with
                    {
                        EntityType = relationEntity.Type,
                        Permission = relationEntity.Relation!,
                    }, subRelation);

                    lookupFunctions.Add((ct) => JoinEntities(main, dependent, ct));
                }
            }

            return await UnionEntities(lookupFunctions, ct);
        };
    }

    private LookupFunction LookupRelationLeaf(LookupEntityRequestInternal req)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            return (await tupleReader.GetRelations(
                    new EntityRelationFilter
                    {
                        Relation = req.Permission,
                        EntityType = req.EntityType
                    },
                    req.SubjectsIds.Select(s => new SubjectFilter
                    {
                        SubjectId = s,
                        SubjectType = req.SubjectType
                    })
                ))
                .Select(x => new RelationOrAttributeTuple(x))
                .ToList();
        };
    }

    private async Task<List<RelationOrAttributeTuple>> JoinEntities(
        Func<List<RelationOrAttributeTuple>, LookupFunction> main,
        LookupFunction dependent, CancellationToken ct
    )
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var dependentResult = await dependent(ct);
        // activity.AddEvent(new ActivityEvent("carregou dependentResult"));
        var mainResult = await main(dependentResult)(ct);
        // activity.AddEvent(new ActivityEvent("carregou mainResult"));

        var result = mainResult.Join(
                dependentResult,
                m => new { Type = m.RelationTuple!.SubjectType, Id = m.RelationTuple!.SubjectId },
                d => new { Type = d.RelationTuple!.EntityType, Id = d.RelationTuple!.EntityId },
                (m, d) => m)
            .ToList();
        // activity.AddEvent(new ActivityEvent("calculou join"));

        return result;
    }

    private async Task<List<RelationOrAttributeTuple>> UnionEntities(List<LookupFunction> functions,
        CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var results = (await Task.WhenAll(functions.Select(f => f(ct))))
            .SelectMany(x => x)
            .ToList();

        return results;
    }

    private async Task<List<RelationOrAttributeTuple>> IntersectEntities(List<LookupFunction> functions,
        CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var overlappingItems = Enumerable.Empty<RelationOrAttributeTuple>();
        var results = await Task.WhenAll(functions.Select(f => f(ct)));

        var count = 0;
        foreach (var result in results)
        {
            overlappingItems = count >= 1
                ? overlappingItems.IntersectBy(
                    result.Select(x => new { Type = x.EntityType, Id = x.EntityId }),
                    x => new { Type = x.EntityType, Id = x.EntityId })
                : result;

            count++;
        }

        var res = overlappingItems.ToList();
        return res;
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

public enum RelationOrAttributeType
{
    Attribute,
    Relation
}