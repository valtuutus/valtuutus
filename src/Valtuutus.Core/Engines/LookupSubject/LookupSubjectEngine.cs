using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.LookupSubject;

internal record LookupSubjectRequestInternal : IWithDepth
{
    private static readonly IDictionary<string, object> EmptyContext = new Dictionary<string, object>(0);

    public required string EntityType { get; init; }
    public required IList<string> EntitiesIds { get; init; }
    public required string Permission { get; init; }
    public required string SubjectType { get; init; }
    public string? SubjectRelation { get; init; }
    public required string FinalSubjectType { get; init; }
    public required string RootEntityType { get; init; }
    public required string RootEntityId { get; init; }
    public SnapToken? SnapToken { get; set; }
    public required int Depth { get; set; } = 10;
    public required IDictionary<string, object> Context { get; set; } = EmptyContext;
}

public sealed class LookupSubjectEngine(
    Schema schema,
    IDataReaderProvider reader) : ILookupSubjectEngine
{
    //<inheritdoc/>
    public async Task<HashSet<string>> Lookup(LookupSubjectRequest req, CancellationToken cancellationToken)
    {
        using var activity =
            DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
                tags: CreateLookupSubjectSpanAttributes(req));
        var snapToken = await SnapTokenUtils.ResolveLatest(reader, req.SnapToken, cancellationToken);
        var internalReq = new LookupSubjectRequestInternal
        {
            Permission = req.Permission,
            EntityType = req.EntityType,
            SubjectType = req.SubjectType,
            EntitiesIds = [req.EntityId],
            FinalSubjectType = req.SubjectType,
            RootEntityId = req.EntityId,
            RootEntityType = req.EntityType,
            SnapToken = snapToken,
            Depth = req.Depth,
            Context = req.Context
        };

        var res = await LookupInternal(internalReq, cancellationToken);
        var tuples = res.RelationsTuples!;
        var hs = new HashSet<string>();
        foreach (ref readonly var t in CollectionsMarshal.AsSpan(tuples)) hs.Add(t.SubjectId);

        activity?.AddEvent(new ActivityEvent("LookupSubjectResult",
            tags: new ActivityTagsCollection(CreateLookupSubjectResultAttributes(hs))));
        return hs;
    }


    private static IEnumerable<KeyValuePair<string, object?>> CreateLookupSubjectResultAttributes(
        HashSet<string> result)
    {
        yield return new KeyValuePair<string, object?>("LookupSubjectResultCount", result.Count);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateLookupSubjectSpanAttributes(
        LookupSubjectRequest req)
    {
        yield return new KeyValuePair<string, object?>("LookupSubjectRequest", req);
    }

    private static readonly List<RelationTuple> _emptyRelationList = new(0);
    private static readonly RelationOrAttributeTuples _emptyTuples = new(_emptyRelationList);
    private static readonly Task<RelationOrAttributeTuples> _failTask = Task.FromResult(_emptyTuples);

    private Task<RelationOrAttributeTuples> LookupInternal(LookupSubjectRequestInternal req, CancellationToken ct)
    {
        if (req.CheckDepthLimit())
            return _failTask;

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

    private Task<RelationOrAttributeTuples> LookupPermission(LookupSubjectRequestInternal req, Permission permission, CancellationToken ct)
    {
        var permNode = permission.Tree;

        return permNode.Type == PermissionNodeType.Expression
            ? LookupExpression(req, permNode.ExpressionNode!, ct)
            : LookupLeaf(req, permNode.LeafNode!, ct);
    }

    private Task<RelationOrAttributeTuples> LookupExpression(LookupSubjectRequestInternal req, PermissionNodeOperation node, CancellationToken ct)
    {
        return node.Operation switch
        {
            PermissionOperation.Intersect => LookupExpressionChildren(req, node.Children, ct, isUnion: false),
            PermissionOperation.Union => LookupExpressionChildren(req, node.Children, ct, isUnion: true),
            PermissionOperation.Negate => LookupNegate(req, node.Children[0], ct),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<RelationOrAttributeTuples> LookupNegate(
        LookupSubjectRequestInternal req, PermissionNode child, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var matching = await (child.Type == PermissionNodeType.Expression
            ? LookupExpression(req, child.ExpressionNode!, ct)
            : LookupLeaf(req, child.LeafNode!, ct));

        var matchingIds = new HashSet<string>();
        if (matching.Type == RelationOrAttributeType.Relation && matching.RelationsTuples is { } tuples)
            foreach (ref readonly var t in CollectionsMarshal.AsSpan(tuples))
                matchingIds.Add(t.SubjectId);

        var complementIds = await reader.GetSubjectIdsExcluding(
            req.FinalSubjectType, matchingIds, req.SnapToken!.Value, ct);

        var syntheticTuples = new List<RelationTuple>(complementIds.Count);
        foreach (var id in complementIds)
            syntheticTuples.Add(new RelationTuple(req.RootEntityType, req.RootEntityId, req.Permission,
                req.FinalSubjectType, id));
        return new RelationOrAttributeTuples(syntheticTuples);
    }

    private async Task<RelationOrAttributeTuples> LookupExpressionChildren(LookupSubjectRequestInternal req,
        List<PermissionNode> children, CancellationToken ct, bool isUnion)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var pool = ArrayPool<Task<RelationOrAttributeTuples>>.Shared;
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

    private Task<RelationOrAttributeTuples> LookupLeaf(LookupSubjectRequestInternal req, PermissionNodeLeaf node, CancellationToken ct)
    {
        return node.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, node.PermissionNode!, ct),
            PermissionNodeLeafType.Expression => CheckLeafExp(req, node.ExpressionNode!, ct),
            _ => throw new InvalidOperationException()
        };
    }

    private Task<RelationOrAttributeTuples> CheckLeafPermission(LookupSubjectRequestInternal req,
        PermissionNodeLeafPermission node, CancellationToken ct)
    {
        if (node.IsIndirect)
            return CheckTupleToUserSet(req, node.UserSet!, node.ComputedUserSet!, ct);
        return LookupComputedUserSet(req, node.Permission, ct);
    }

    private async Task<RelationOrAttributeTuples> CheckLeafExp(LookupSubjectRequestInternal req,
        PermissionNodeLeafExp node, CancellationToken ct)
    {
        var fn = schema.Functions[node.FunctionName];

        if (fn is null)
        {
            throw new InvalidOperationException();
        }

        if (!node.IsContextValid(req.Context))
        {
            return new RelationOrAttributeTuples(new List<RelationTuple>());
        }

        var attributeArguments = node.GetArgsAttributesNames();

        var attributes = await reader.GetAttributesWithEntityIds(
            new EntityAttributesFilter
            {
                Attributes = attributeArguments, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
            }, req.EntitiesIds, ct);

        using var paramToArgMap = fn.CreateParamToArgMap(node.Args);

        return new RelationOrAttributeTuples(EvaluateExpressionMatches(attributes, req, fn, paramToArgMap));
    }

    private List<AttributeTuple> EvaluateExpressionMatches(
        IReadOnlyDictionary<(string AttributeName, string EntityId), AttributeTuple> attributes,
        LookupSubjectRequestInternal req,
        Function fn,
        PooledDictionary<FunctionParameter, PermissionNodeExpArgument> paramToArgMap)
    {
        var result = new List<AttributeTuple>();

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
                result.Add(attr);
            }
        }

        return result;
    }

    private async Task<RelationOrAttributeTuples> CheckTupleToUserSet(LookupSubjectRequestInternal req,
        string tupleSetRelation, string computedUserSetRelation, CancellationToken ct)
    {
        var relation = schema.GetRelation(req.EntityType, tupleSetRelation);
        var pool = ArrayPool<Task<RelationOrAttributeTuples>>.Shared;
        var buffer = pool.Rent(relation.Entities.Count);
        var count = 0;
        try
        {
            foreach (var entity in relation.Entities)
            {
                var pooled0 = await reader.GetRelationsWithEntityIds(
                    new EntityRelationFilter
                    {
                        Relation = relation.Name, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
                    },
                    entity.Type,
                    req.EntitiesIds,
                    entity.Relation,
                    ct
                );

                buffer[count++] = LookupInternal(req with
                {
                    EntityType = entity.Type,
                    Permission = computedUserSetRelation,
                    EntitiesIds = ToSubjectIdList(pooled0.Transfer())
                }, ct);
            }

            return await UnionEntities(buffer, count);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private Task<RelationOrAttributeTuples> LookupComputedUserSet(LookupSubjectRequestInternal req,
        string computedUserSetRelation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return LookupInternal(req with { Permission = computedUserSetRelation }, ct);
    }

    private async Task<RelationOrAttributeTuples> LookupAttribute(LookupSubjectRequestInternal req,
        Schemas.Attribute attribute, CancellationToken ct)
    {
        var res = (await reader.GetAttributesWithEntityIds(
                    new AttributeFilter
                    {
                        Attribute = attribute.Name, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
                    }, req.EntitiesIds, ct))
                .Where(x => x.Value.TryGetValue<bool>(out var b) && b)
                .ToList();

        return new RelationOrAttributeTuples(res);
    }

    private Task<RelationOrAttributeTuples> LookupRelation(LookupSubjectRequestInternal req, Relation relation, CancellationToken ct)
    {
        if (!relation.EntityTypes.Contains(req.FinalSubjectType) && !relation.HasSubRelationPaths)
            return _failTask;

        return LookupRelationCore(req, relation, ct);
    }

    private async Task<RelationOrAttributeTuples> LookupRelationCore(LookupSubjectRequestInternal req, Relation relation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var pool = ArrayPool<Task<RelationOrAttributeTuples>>.Shared;
        var buffer = pool.Rent(relation.Entities.Count);
        var count = 0;
        try
        {
            foreach (var relationEntity in relation.Entities)
            {
                if (relationEntity.Type == req.FinalSubjectType)
                {
                    buffer[count++] = LookupRelationLeaf(req with { SubjectType = relationEntity.Type, }, ct);
                    continue;
                }

                var subRelation = relationEntity.Relation is null
                    ? null
                    : schema.GetRelation(relationEntity.Type, relationEntity.Relation);

                if (subRelation is not null)
                {
                    var pooled1 = await reader.GetRelationsWithEntityIds(
                        new EntityRelationFilter
                        {
                            Relation = req.Permission, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
                        },
                        relationEntity.Type,
                        req.EntitiesIds,
                        subRelation.Name,
                        ct
                    );

                    buffer[count++] = LookupRelation(
                        req with
                        {
                            EntityType = relationEntity.Type,
                            Permission = relationEntity.Relation!,
                            EntitiesIds = ToSubjectIdList(pooled1.Transfer())
                        }, subRelation, ct);
                }
            }

            return await UnionEntities(buffer, count);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private async Task<RelationOrAttributeTuples> LookupRelationLeaf(LookupSubjectRequestInternal req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        var pooled = await reader.GetRelationsWithEntityIds(
            new EntityRelationFilter
            {
                Relation = req.Permission, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
            },
            req.SubjectType,
            req.EntitiesIds,
            req.SubjectRelation,
            ct
        );

        return new RelationOrAttributeTuples(pooled.Transfer());
    }


    private static async Task<RelationOrAttributeTuples> UnionEntities(
        Task<RelationOrAttributeTuples>[] buffer, int count)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var results = await Task.WhenAll(new ArraySegment<Task<RelationOrAttributeTuples>>(buffer, 0, count));

        var totalCount = 0;
        foreach (var r in results)
            if (r.Type == RelationOrAttributeType.Relation) totalCount += r.RelationsTuples!.Count;
        var relations = new List<RelationTuple>(totalCount);
        foreach (var r in results)
            if (r.Type == RelationOrAttributeType.Relation) relations.AddRange(CollectionsMarshal.AsSpan(r.RelationsTuples!));

        return new RelationOrAttributeTuples(relations);
    }

    private static async Task<RelationOrAttributeTuples> IntersectEntities(
        Task<RelationOrAttributeTuples>[] buffer, int count)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var results = await Task.WhenAll(new ArraySegment<Task<RelationOrAttributeTuples>>(buffer, 0, count));

        HashSet<RelationTuple>? hashSet = null;
        foreach (var result in results)
        {
            if (result.Type == RelationOrAttributeType.Attribute)
            {
                if (result.AttributesTuples!.Count == 0)
                    return _emptyTuples;
                continue;
            }

            var rels = result.RelationsTuples!;
            if (hashSet is null)
            {
                hashSet = new HashSet<RelationTuple>(rels, RelationTupleComparer.Instance);
                continue;
            }
            hashSet.IntersectWith(rels);
            if (hashSet.Count == 0)
                return _emptyTuples;
        }

        if (hashSet is null) return _emptyTuples;
        return new RelationOrAttributeTuples([.. hashSet]);
    }

    private static List<string> ToSubjectIdList(List<RelationTuple> tuples)
    {
        var list = new List<string>(tuples.Count);
        foreach (ref readonly var t in CollectionsMarshal.AsSpan(tuples)) list.Add(t.SubjectId);
        return list;
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

internal enum RelationOrAttributeType
{
    Attribute,
    Relation
}
