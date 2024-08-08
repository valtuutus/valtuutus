using System.Diagnostics;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Schemas;
using LookupFunction =
    System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<
        System.Collections.Generic.List<Valtuutus.Core.Engines.LookupEntity.RelationOrAttributeTuple>>>;

namespace Valtuutus.Core.Engines.LookupEntity;

internal record LookupEntityRequestInternal
{
    public required string EntityType { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public required IList<string> SubjectsIds { get; init; }
    public string? SubjectRelation { get; init; }
    public required string FinalSubjectType { get; init; }
    public required string FinalSubjectId { get; init; }
}

public sealed class LookupEntityEngine(
    Schema schema,
    IDataReaderProvider reader) : ILookupEntityEngine
{
    /// <summary>
    /// The LookupEntity method lets you ask "Which resources of type T can entity:X do action Y?"
    /// </summary>
    /// <param name="req">The object containing information about the question being asked</param>
    /// <param name="ct">Cancellation Token</param>
    /// <returns>The list of ids of the entities</returns>
    public async Task<HashSet<string>> LookupEntity(LookupEntityRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateLookupEntitySpanAttributes(req));
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
        var hs =  new HashSet<string>(res.Select(x => x.EntityId).OrderBy(x => x));
        activity?.AddEvent(new ActivityEvent("LookupEntityResult", tags: new ActivityTagsCollection(CreateLookupEntityResultAttributes(hs))));
        return hs;
    }
    
    
    private static IEnumerable<KeyValuePair<string, object?>> CreateLookupEntityResultAttributes(HashSet<string> result)
    {
        yield return new KeyValuePair<string, object?>("LookupEntityResultCount", result.Count);
    }
    
    private static IEnumerable<KeyValuePair<string, object?>> CreateLookupEntitySpanAttributes(LookupEntityRequest req)
    {
        yield return new KeyValuePair<string, object?>("LookupEntityRequest", req);
    }

    private LookupFunction LookupEntityInternal(LookupEntityRequestInternal req)
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
        return node.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, node.PermissionNode!),
            PermissionNodeLeafType.AttributeExpression => CheckLeafAttributeExp(req, node.ExpressionNode!),
            _ => throw new InvalidOperationException()
        };
    }
    
    private LookupFunction CheckLeafPermission(LookupEntityRequestInternal req, PermissionNodeLeafPermission node)
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
    
    private LookupFunction CheckLeafAttributeExp(LookupEntityRequestInternal req, PermissionNodeLeafAttributeExp node)
    {
        return async (ct) =>
        {
            using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
            var attrName = node.AttributeName;

            return (await reader.GetAttributes(new EntityAttributeFilter
                {
                    Attribute = attrName,
                    EntityType = req.EntityType
                }, ct))
                .Where(AttrEvaluator)
                .Select(x => new RelationOrAttributeTuple(x))
                .ToList();

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

    private LookupFunction CheckTupleToUserSet(LookupEntityRequestInternal req, string tupleSetRelation,
        string computedUserSetRelation)
    {
        return async (ct) =>
        {
            var relation = schema.GetRelation(req.EntityType, tupleSetRelation);
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
            return (await reader.GetAttributes(new EntityAttributeFilter
                {
                    Attribute = attribute.Name,
                    EntityType = req.EntityType
                }, ct))
                .Where(a => a.Value.TryGetValue(out bool b) && b)
                .Select(x => new RelationOrAttributeTuple(x))
                .ToList();
        };
    }

    private LookupFunction LookupRelation(LookupEntityRequestInternal req, Relation relation)
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

                var subRelation = relationEntity.Relation is null ? null : schema.GetRelation(relationEntity.Type, relationEntity.Relation);

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

                    lookupFunctions.Add((ct1) => JoinEntities(main, dependent, ct1));
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
            return (await reader.GetRelationsWithSubjectsIds(
                    new EntityRelationFilter
                    {
                        Relation = req.Permission,
                        EntityType = req.EntityType
                    },
                    req.SubjectsIds,
                    req.SubjectType,
                    ct
                ))
                .Select(x => new RelationOrAttributeTuple(x))
                .ToList();
        };
    }

    private static async Task<List<RelationOrAttributeTuple>> JoinEntities(
        Func<List<RelationOrAttributeTuple>, LookupFunction> main,
        LookupFunction dependent, CancellationToken ct
    )
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var dependentResult = await dependent(ct);
        var mainResult = await main(dependentResult)(ct);

        var result = mainResult.Join(
                dependentResult,
                m => new { Type = m.RelationTuple!.SubjectType, Id = m.RelationTuple!.SubjectId },
                d => new { Type = d.RelationTuple!.EntityType, Id = d.RelationTuple!.EntityId },
                (m, d) => m)
            .ToList();

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
                ? overlappingItems.Intersect(
                    result, RelationOrAttributeComparer.Instance)
                : result;

            count++;
        }

        var res = overlappingItems.ToList();
        return res;
    }
}

internal record RelationOrAttributeTuple
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

internal sealed class RelationOrAttributeComparer : IEqualityComparer<RelationOrAttributeTuple>
{
    private RelationOrAttributeComparer()
    {
    }

    internal static IEqualityComparer<RelationOrAttributeTuple> Instance { get; } = new RelationOrAttributeComparer();

    public bool Equals(RelationOrAttributeTuple? x, RelationOrAttributeTuple? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (ReferenceEquals(x, null)) return false;
        if (ReferenceEquals(y, null)) return false;
        if (x.GetType() != y.GetType()) return false;
        return x.EntityType == y.EntityType && x.EntityId == y.EntityId;
    }

    public int GetHashCode(RelationOrAttributeTuple obj)
    {
        unchecked
        {
            return (obj.EntityType.GetHashCode() * 397) ^ obj.EntityId.GetHashCode();
        }
    }
}

internal enum RelationOrAttributeType
{
    Attribute,
    Relation
}