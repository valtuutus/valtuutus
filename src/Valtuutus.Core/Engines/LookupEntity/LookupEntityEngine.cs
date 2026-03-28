using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupEntity;

internal record LookupEntityRequestInternal : IWithDepth
{
    private static readonly IDictionary<string, object> EmptyContext = new Dictionary<string, object>(0);

    public required string EntityType { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public required IList<string> SubjectsIds { get; init; }
    public string? SubjectRelation { get; init; }
    public required string FinalSubjectType { get; init; }
    public required string FinalSubjectId { get; init; }
    public SnapToken? SnapToken { get; set; }
    public required int Depth { get; set; } = 10;
    public required IDictionary<string, object> Context { get; set; } = EmptyContext;
}

public sealed class LookupEntityEngine(
    Schema schema,
    IDataReaderProvider reader) : ILookupEntityEngine
{
    //<inheritdoc/>
    public async Task<HashSet<string>> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        using var activity =
            DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
                tags: CreateLookupEntitySpanAttributes(req));

        await SnapTokenUtils.LoadLatestSnapToken(reader, req, cancellationToken);
        var internalReq = new LookupEntityRequestInternal
        {
            Permission = req.Permission,
            EntityType = req.EntityType,
            SubjectType = req.SubjectType,
            SubjectsIds = [req.SubjectId],
            FinalSubjectType = req.SubjectType,
            FinalSubjectId = req.SubjectId,
            SnapToken = req.SnapToken,
            Depth = req.Depth,
            Context = req.Context
        };

        var res = await LookupEntityInternal(internalReq, cancellationToken);
        var hs = new HashSet<string>(res.Count);
        foreach (var r in res) hs.Add(r.EntityId);
        ListPool<RelationOrAttributeTuple>.Return(res);
        activity?.AddEvent(new ActivityEvent("LookupEntityResult",
            tags: new ActivityTagsCollection(CreateLookupEntityResultAttributes(hs))));
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

    private static Task<List<RelationOrAttributeTuple>> EmptyPooledListTask() =>
        Task.FromResult(ListPool<RelationOrAttributeTuple>.Rent());

    private Task<List<RelationOrAttributeTuple>> LookupEntityInternal(LookupEntityRequestInternal req, CancellationToken ct)
    {
        if (req.CheckDepthLimit())
            return EmptyPooledListTask();

        req.DecreaseDepth();

        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return schema.GetRelationType(req.EntityType, req.Permission) switch
        {
            RelationType.DirectRelation => LookupRelation(req, schema.GetRelation(req.EntityType, req.Permission), ct),
            RelationType.Permission => LookupPermission(req, schema.GetPermission(req.EntityType, req.Permission), ct),
            RelationType.Attribute => LookupAttribute(req, schema.GetAttribute(req.EntityType, req.Permission), ct),
            _ => throw new InvalidOperationException()
        };
    }

    private Task<List<RelationOrAttributeTuple>> LookupPermission(LookupEntityRequestInternal req, Permission permission, CancellationToken ct)
    {
        var permNode = permission.Tree;

        return permNode.Type == PermissionNodeType.Expression
            ? LookupExpression(req, permNode.ExpressionNode!, ct)
            : LookupLeaf(req, permNode.LeafNode!, ct);
    }

    private Task<List<RelationOrAttributeTuple>> LookupExpression(LookupEntityRequestInternal req, PermissionNodeOperation node, CancellationToken ct)
    {
        return node.Operation switch
        {
            PermissionOperation.Intersect => LookupExpressionChildren(req, node.Children, ct, isUnion: false),
            PermissionOperation.Union => LookupExpressionChildren(req, node.Children, ct, isUnion: true),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<List<RelationOrAttributeTuple>> LookupExpressionChildren(LookupEntityRequestInternal req,
        List<PermissionNode> children, CancellationToken ct, bool isUnion)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var pool = ArrayPool<Task<List<RelationOrAttributeTuple>>>.Shared;
        var buffer = pool.Rent(children.Count);
        try
        {
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                buffer[i] = child.Type == PermissionNodeType.Expression
                    ? LookupExpression(req, child.ExpressionNode!, ct)
                    : LookupLeaf(req, child.LeafNode!, ct);
            }

            return isUnion
                ? await UnionEntities(buffer, children.Count)
                : await IntersectEntities(buffer, children.Count);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private Task<List<RelationOrAttributeTuple>> LookupLeaf(LookupEntityRequestInternal req, PermissionNodeLeaf node, CancellationToken ct)
    {
        return node.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, node.PermissionNode!, ct),
            PermissionNodeLeafType.Expression => CheckLeafExp(req, node.ExpressionNode!, ct),
            _ => throw new InvalidOperationException()
        };
    }

    private Task<List<RelationOrAttributeTuple>> CheckLeafPermission(LookupEntityRequestInternal req,
        PermissionNodeLeafPermission node, CancellationToken ct)
    {
        if (node.IsIndirect)
            return CheckTupleToUserSet(req, node.UserSet!, node.ComputedUserSet!, ct);
        return LookupComputedUserSet(req, node.Permission, ct);
    }

    private async Task<List<RelationOrAttributeTuple>> CheckLeafExp(LookupEntityRequestInternal req,
        PermissionNodeLeafExp node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        var fn = schema.Functions[node.FunctionName];

        if (fn is null)
        {
            throw new InvalidOperationException();
        }

        if (!node.IsContextValid(req.Context))
        {
            return ListPool<RelationOrAttributeTuple>.Rent();
        }

        var attributeArguments = node.GetArgsAttributesNames();

        var attributes = await reader.GetAttributes(
            new EntityAttributesFilter
            {
                Attributes = attributeArguments,
                EntityType = req.EntityType,
                SnapToken = req.SnapToken
            }, ct);

        using var paramToArgMap = fn.CreateParamToArgMap(node.Args);

        return EvaluateExpressionMatches(attributes, req, fn, paramToArgMap);
    }

    private List<RelationOrAttributeTuple> EvaluateExpressionMatches(
        IReadOnlyDictionary<(string AttributeName, string EntityId), AttributeTuple> attributes,
        LookupEntityRequestInternal req,
        Function fn,
        PooledDictionary<FunctionParameter, PermissionNodeExpArgument> paramToArgMap)
    {
        var result = ListPool<RelationOrAttributeTuple>.Rent();

        foreach (var attr in attributes.Values)
        {
            using var fnArgs = paramToArgMap.ToLambdaArgs(
                static (arg, state) =>
                {
                    var (attrs, entityId, entityType, sch) = state;
                    if (!attrs.TryGetValue((arg.AttributeName, entityId), out var a))
                        return null;
                    return a.GetValue(sch.GetAttribute(entityType, arg.AttributeName).Type);
                },
                (attributes, attr.EntityId, req.EntityType, schema),
                req.Context);

            if (fn.Lambda(fnArgs.Dictionary))
            {
                result.Add(new RelationOrAttributeTuple(attr));
            }
        }

        return result;
    }

    private async Task<List<RelationOrAttributeTuple>> CheckTupleToUserSet(LookupEntityRequestInternal req,
        string tupleSetRelation, string computedUserSetRelation, CancellationToken ct)
    {
        var relation = schema.GetRelation(req.EntityType, tupleSetRelation);
        var pool = ArrayPool<Task<List<RelationOrAttributeTuple>>>.Shared;
        var buffer = pool.Rent(relation.Entities.Count);
        var count = 0;
        try
        {
            foreach (var entity in relation.Entities)
            {
                var dependent = LookupEntityInternal(req with
                {
                    EntityType = entity.Type, Permission = computedUserSetRelation,
                }, ct);

                var capturedEntity = entity;
                var capturedReq = req;
                var capturedTupleSetRelation = tupleSetRelation;
                buffer[count++] = JoinEntities(
                    relatedTuples =>
                    {
                        using var activityMain = DefaultActivitySource.InternalSourceInstance.StartActivity("join main FN");
                        if (relatedTuples.Count > 0)
                        {
                            return LookupRelationLeaf(capturedReq with
                            {
                                Permission = capturedTupleSetRelation,
                                EntityType = capturedReq.EntityType,
                                SubjectType = capturedEntity.Type,
                                SubjectsIds = ToEntityIdList(relatedTuples),
                                SubjectRelation = capturedEntity.Relation,
                                Depth = capturedReq.Depth
                            }, ct);
                        }

                        return EmptyPooledListTask();
                    },
                    dependent);
            }

            return await UnionEntities(buffer, count);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private Task<List<RelationOrAttributeTuple>> LookupComputedUserSet(LookupEntityRequestInternal req,
        string computedUserSetRelation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return LookupEntityInternal(req with { Permission = computedUserSetRelation }, ct);
    }

    private async Task<List<RelationOrAttributeTuple>> LookupAttribute(LookupEntityRequestInternal req,
        Schemas.Attribute attribute, CancellationToken ct)
    {
        var attrs = await reader.GetAttributes(
            new EntityAttributeFilter
            {
                Attribute = attribute.Name, EntityType = req.EntityType, SnapToken = req.SnapToken
            }, ct);
        var result = ListPool<RelationOrAttributeTuple>.Rent();
        foreach (var a in attrs)
            if (a.Value.TryGetValue(out bool b) && b)
                result.Add(new RelationOrAttributeTuple(a));
        return result;
    }

    private Task<List<RelationOrAttributeTuple>> LookupRelation(LookupEntityRequestInternal req, Relation relation, CancellationToken ct)
    {
        if (!relation.EntityTypes.Contains(req.FinalSubjectType) && !relation.HasSubRelationPaths)
            return EmptyPooledListTask();

        return LookupRelationCore(req, relation, ct);
    }

    private async Task<List<RelationOrAttributeTuple>> LookupRelationCore(LookupEntityRequestInternal req, Relation relation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var pool = ArrayPool<Task<List<RelationOrAttributeTuple>>>.Shared;
        var buffer = pool.Rent(relation.Entities.Count);
        var count = 0;
        try
        {
            foreach (var relationEntity in relation.Entities)
            {
                if (relationEntity.Type == req.FinalSubjectType)
                {
                    buffer[count++] = LookupRelationLeaf(req with
                    {
                        SubjectType = relationEntity.Type, SubjectsIds = [req.FinalSubjectId], Depth = req.Depth
                    }, ct);
                    continue;
                }

                var subRelation = relationEntity.Relation is null
                    ? null
                    : schema.GetRelation(relationEntity.Type, relationEntity.Relation);

                if (subRelation is not null)
                {
                    var capturedRelationEntity = relationEntity;
                    var capturedRelation = relation;
                    var capturedReq = req;
                    var dependent = LookupRelation(req with
                    {
                        EntityType = relationEntity.Type, Permission = relationEntity.Relation!,
                    }, subRelation, ct);

                    buffer[count++] = JoinEntities(
                        relatedTuples =>
                        {
                            using var activityMain =
                                DefaultActivitySource.InternalSourceInstance.StartActivity("join main FN");
                            if (relatedTuples.Count > 0)
                            {
                                return LookupRelationLeaf(capturedReq with
                                {
                                    Permission = capturedRelation.Name,
                                    EntityType = capturedReq.EntityType,
                                    SubjectType = capturedRelationEntity.Type,
                                    SubjectsIds = ToEntityIdList(relatedTuples),
                                    SubjectRelation = capturedRelationEntity.Relation,
                                    Depth = capturedReq.Depth
                                }, ct);
                            }

                            return EmptyPooledListTask();
                        },
                        dependent);
                }
            }

            return await UnionEntities(buffer, count);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private async Task<List<RelationOrAttributeTuple>> LookupRelationLeaf(LookupEntityRequestInternal req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        var relations = await reader.GetRelationsWithSubjectsIds(
            new EntityRelationFilter
            {
                Relation = req.Permission, EntityType = req.EntityType, SnapToken = req.SnapToken
            },
            req.SubjectsIds,
            req.SubjectType,
            ct);
        var result = ListPool<RelationOrAttributeTuple>.Rent();
        foreach (var x in relations) result.Add(new RelationOrAttributeTuple(x));
        return result;
    }

    private static async Task<List<RelationOrAttributeTuple>> JoinEntities(
        Func<List<RelationOrAttributeTuple>, Task<List<RelationOrAttributeTuple>>> main,
        Task<List<RelationOrAttributeTuple>> dependent
    )
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var dependentResult = await dependent;
        var mainResult = await main(dependentResult);

        var dependentSet = new HashSet<(string Type, string Id)>(dependentResult.Count);
        foreach (var d in dependentResult)
        {
            if (d.Type == RelationOrAttributeType.Relation)
                dependentSet.Add((d.RelationTuple!.EntityType, d.RelationTuple!.EntityId));
            else
                dependentSet.Add((d.AttributeTuple!.EntityType, d.AttributeTuple!.EntityId));
        }
        ListPool<RelationOrAttributeTuple>.Return(dependentResult);

        var result = ListPool<RelationOrAttributeTuple>.Rent();
        foreach (var m in mainResult)
            if (dependentSet.Contains((m.RelationTuple!.SubjectType, m.RelationTuple!.SubjectId)))
                result.Add(m);
        ListPool<RelationOrAttributeTuple>.Return(mainResult);

        return result;
    }

    private static async Task<List<RelationOrAttributeTuple>> UnionEntities(
        Task<List<RelationOrAttributeTuple>>[] buffer, int count)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var results = await Task.WhenAll(new ArraySegment<Task<List<RelationOrAttributeTuple>>>(buffer, 0, count));

        var merged = ListPool<RelationOrAttributeTuple>.Rent();
        foreach (var r in results)
        {
            merged.AddRange(r);
            ListPool<RelationOrAttributeTuple>.Return(r);
        }
        return merged;
    }

    private static async Task<List<RelationOrAttributeTuple>> IntersectEntities(
        Task<List<RelationOrAttributeTuple>>[] buffer, int count)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var results = await Task.WhenAll(new ArraySegment<Task<List<RelationOrAttributeTuple>>>(buffer, 0, count));

        if (results.Length == 0)
            return ListPool<RelationOrAttributeTuple>.Rent();

        var hashSet = new HashSet<RelationOrAttributeTuple>(results[0], RelationOrAttributeComparer.Instance);
        ListPool<RelationOrAttributeTuple>.Return(results[0]);
        for (var i = 1; i < results.Length; i++)
        {
            hashSet.IntersectWith(results[i]);
            ListPool<RelationOrAttributeTuple>.Return(results[i]);
            if (hashSet.Count == 0)
                return ListPool<RelationOrAttributeTuple>.Rent();
        }

        var result = ListPool<RelationOrAttributeTuple>.Rent();
        foreach (var item in hashSet) result.Add(item);
        return result;
    }

    private static List<string> ToEntityIdList(List<RelationOrAttributeTuple> tuples)
    {
        var list = new List<string>(tuples.Count);
        foreach (var t in tuples) list.Add(t.EntityId);
        return list;
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
        if (x is null || y is null) return false;
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
