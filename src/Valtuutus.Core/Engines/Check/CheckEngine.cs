using System.Buffers;
using System.Diagnostics;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check;

public enum RelationType
{
    None,
    DirectRelation,
    Permission,
    Attribute
}

public sealed class CheckEngine(IDataReaderProvider reader, Schema schema) : ICheckEngine
{
    //<inheritdoc/>
    public async Task<bool> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        using var activity =
            DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateCheckSpanAttributes(req));

        await SnapTokenUtils.LoadLatestSnapToken(reader, req, cancellationToken);
        var val = await CheckInternal(req, cancellationToken);
        activity?.AddEvent(new ActivityEvent("CheckFinished",
            tags: new ActivityTagsCollection(CreateCheckResultAttributes(val))));
        return val;
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateCheckResultAttributes(bool result)
    {
        yield return new KeyValuePair<string, object?>("CheckResult", result);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateCheckSpanAttributes(CheckRequest req)
    {
        yield return new KeyValuePair<string, object?>("CheckRequest", req);
    }


    //<inheritdoc/>
    public async Task<Dictionary<string, bool>> SubjectPermission(SubjectPermissionRequest req,
        CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
            tags: CreateSubjectPermissionSpanAttributes(req));
        var permissions = schema.GetPermissions(req.EntityType);
        await SnapTokenUtils.LoadLatestSnapToken(reader, req, cancellationToken);

        var count = permissions.Count;
        var names = new string[count];
        var tasks = new Task<bool>[count];

        var i = 0;
        foreach (var perm in permissions)
        {
            names[i] = perm.Name;
            tasks[i] = CheckInternal(new CheckRequest
            {
                EntityType = req.EntityType,
                EntityId = req.EntityId,
                Permission = perm.Name,
                SubjectType = req.SubjectType,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, cancellationToken);
            i++;
        }

        await Task.WhenAll(tasks);

        var dict = new Dictionary<string, bool>(count);
        for (var j = 0; j < count; j++)
            dict[names[j]] = tasks[j].Result;

        activity?.AddEvent(new ActivityEvent("SubjectPermissionFinished",
            tags: new ActivityTagsCollection(CreateSubjectPermissionResultAttributes(dict))));
        return dict;
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateSubjectPermissionResultAttributes(
        Dictionary<string, bool> result)
    {
        foreach (var (k, v) in result)
            yield return new KeyValuePair<string, object?>(k, v);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateSubjectPermissionSpanAttributes(
        SubjectPermissionRequest req)
    {
        yield return new KeyValuePair<string, object?>("SubjectPermissionRequest", req);
    }

    private Task<bool> CheckInternal(CheckRequest req, CancellationToken ct)
    {
        if (req.CheckDepthLimit())
            return Task.FromResult(false);

        req.DecreaseDepth();

        return schema.GetRelationType(req.EntityType, req.Permission) switch
        {
            RelationType.DirectRelation => CheckRelation(req, ct),
            RelationType.Permission => CheckPermission(req, schema.GetPermission(req.EntityType, req.Permission), ct),
            RelationType.Attribute => CheckAttribute(req, ct),
            _ => Task.FromResult(false)
        };
    }

    private Task<bool> CheckPermission(CheckRequest req, Permission permission, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity("CheckPermission");

        var permissionNode = permission!.Tree;

        return permissionNode.Type == PermissionNodeType.Expression
            ? CheckExpression(req, permissionNode, ct)
            : CheckLeaf(req, permissionNode, ct);
    }

    private async Task<bool> CheckAttribute(CheckRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        var attribute = await reader.GetAttribute(
            new EntityAttributeFilter
            {
                Attribute = req.Permission,
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                SnapToken = req.SnapToken
            }, ct);

        if (attribute is null)
            return false;

        return attribute.Value.GetValue<bool>();
    }

    private Task<bool> CheckExpression(CheckRequest req, PermissionNode node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return node.ExpressionNode!.Operation switch
        {
            PermissionOperation.Intersect => CheckExpressionChild(req, node.ExpressionNode!.Children, ct, isUnion: false),
            PermissionOperation.Union => CheckExpressionChild(req, node.ExpressionNode!.Children, ct, isUnion: true),
            _ => throw new InvalidOperationException()
        };
    }

    private Task<bool> CheckExpressionChild(CheckRequest req, List<PermissionNode> children, CancellationToken ct, bool isUnion)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var checkFunctions = new List<Func<CancellationToken, Task<bool>>>(capacity: children.Count);
        foreach (var child in children)
        {
            switch (child.Type)
            {
                case PermissionNodeType.Expression:
                    var exprReq = req;
                    var exprNode = child;
                    checkFunctions.Add(innerCt => CheckExpression(exprReq, exprNode, innerCt));
                    break;
                case PermissionNodeType.Leaf:
                    var leafReq = req;
                    var leafNode = child;
                    checkFunctions.Add(innerCt => CheckLeaf(leafReq, leafNode, innerCt));
                    break;
            }
        }

        return isUnion ? CheckUnion(checkFunctions, ct) : CheckIntersect(checkFunctions, ct);
    }

    private Task<bool> CheckLeaf(CheckRequest req, PermissionNode node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return node.LeafNode!.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, node.LeafNode!.PermissionNode!, ct),
            PermissionNodeLeafType.Expression => CheckLeafFn(req, node.LeafNode!.ExpressionNode!, ct),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<bool> CheckLeafFn(CheckRequest req, PermissionNodeLeafExp node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var fn = schema.Functions[node.FunctionName];

        if (fn is null)
            throw new InvalidOperationException();

        if (!node.IsContextValid(req.Context))
            return false;

        var attributeArguments = node.GetArgsAttributesNames();

        var attributes = await reader.GetAttributes(
            new EntityAttributesFilter
            {
                Attributes = attributeArguments,
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                SnapToken = req.SnapToken
            }, ct);

        using var paramToArg = fn.CreateParamToArgMap(node.Args);

        using var fnArgs = paramToArg.ToLambdaArgs(
            static (arg, state) =>
            {
                var (attrs, entityId, entityType, sch) = state;
                if (!attrs.TryGetValue((arg.AttributeName, entityId), out var attr))
                    return null;
                return attr.GetValue(sch.GetAttribute(entityType, arg.AttributeName).Type);
            },
            (attributes, req.EntityId, req.EntityType, schema),
            req.Context);

        return fn.Lambda(fnArgs.Dictionary);
    }

    private Task<bool> CheckLeafPermission(CheckRequest req, PermissionNodeLeafPermission node, CancellationToken ct)
    {
        if (node.IsIndirect)
            return CheckTupleToUserSet(req, node.UserSet!, node.ComputedUserSet!, ct);
        return CheckComputedUserSet(req, node.Permission, ct);
    }

    private Task<bool> CheckComputedUserSet(CheckRequest req, string computedUserSetRelation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return CheckInternal(req with { Permission = computedUserSetRelation }, ct);
    }

    private async Task<bool> CheckTupleToUserSet(CheckRequest req, string tupleSetRelation,
        string computedUserSetRelation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        var relations = await reader.GetRelations(
            new RelationTupleFilter
            {
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                Relation = tupleSetRelation,
                SnapToken = req.SnapToken
            }, ct);

        if (relations.Count == 0) return false;

        var pool = ArrayPool<Func<CancellationToken, Task<bool>>>.Shared;
        var buffer = pool.Rent(relations.Count);
        try
        {
            for (var i = 0; i < relations.Count; i++)
            {
                var relation = relations[i];
                var innerReq = new CheckRequest
                {
                    EntityType = relation.SubjectType,
                    EntityId = relation.SubjectId,
                    Permission = relation.SubjectRelation,
                    SubjectId = req.SubjectId,
                    SnapToken = req.SnapToken,
                    Depth = req.Depth
                };
                var captured = computedUserSetRelation;
                buffer[i] = innerCt => CheckComputedUserSet(innerReq, captured, innerCt);
            }
            return await CheckUnion(buffer, relations.Count, ct);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private async Task<bool> CheckRelation(CheckRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var filter = new RelationTupleFilter
        {
            EntityId = req.EntityId,
            EntityType = req.EntityType,
            Relation = req.Permission,
            SnapToken = req.SnapToken
        };

        // Phase 1: cheap point-check for a direct match.
        // Avoids fetching the full relation list when the subject is a direct member.
        if (await reader.HasDirectRelation(filter, req.SubjectId, ct))
            return true;

        // Phase 2: fetch only indirect tuples for recursion.
        var indirectRelations = await reader.GetIndirectRelations(filter, ct);

        if (indirectRelations.Count == 0) return false;

        var pool = ArrayPool<Func<CancellationToken, Task<bool>>>.Shared;
        var buffer = pool.Rent(indirectRelations.Count);
        var count = 0;
        try
        {
            foreach (var relation in indirectRelations)
            {
                var innerReq = new CheckRequest
                {
                    EntityType = relation.SubjectType,
                    EntityId = relation.SubjectId,
                    Permission = relation.SubjectRelation,
                    SubjectId = req.SubjectId,
                    SnapToken = req.SnapToken,
                    Depth = req.Depth
                };
                buffer[count++] = innerCt => CheckInternal(innerReq, innerCt);
            }

            return await CheckUnion(buffer, count, ct);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private static Task<bool> CheckUnion(Func<CancellationToken, Task<bool>>[] functions, int count, CancellationToken ct)
    {
        if (count == 0) return Task.FromResult(false);
        if (count == 1) return functions[0](ct);
        return CheckUnionCore(functions, count, ct);
    }

    private static Task<bool> CheckUnion(List<Func<CancellationToken, Task<bool>>> functions, CancellationToken ct)
    {
        if (functions.Count == 0) return Task.FromResult(false);
        if (functions.Count == 1) return functions[0](ct);
        return CheckUnionCore(functions, functions.Count, ct);
    }

    private static async Task<bool> CheckUnionCore(IReadOnlyList<Func<CancellationToken, Task<bool>>> functions, int count, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        var tasks = new Task<bool>[count];
        for (var i = 0; i < count; i++)
        {
            tasks[i] = functions[i](cancellationToken).ContinueWith(
                static (t, state) =>
                {
                    if (t.Result) ((CancellationTokenSource)state!).Cancel();
                    return t.Result;
                },
                innerCts, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current);
        }

        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return Array.Exists(results, static b => b);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private static Task<bool> CheckIntersect(List<Func<CancellationToken, Task<bool>>> functions, CancellationToken ct)
    {
        if (functions.Count == 0) return Task.FromResult(true);
        if (functions.Count == 1) return functions[0](ct);
        return CheckIntersectCore(functions, ct);
    }

    private static async Task<bool> CheckIntersectCore(List<Func<CancellationToken, Task<bool>>> functions, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        var tasks = new Task<bool>[functions.Count];
        for (var i = 0; i < functions.Count; i++)
        {
            tasks[i] = functions[i](cancellationToken).ContinueWith(
                static (t, state) =>
                {
                    if (!t.Result) ((CancellationTokenSource)state!).Cancel();
                    return t.Result;
                },
                innerCts, cancellationToken, TaskContinuationOptions.NotOnFaulted, TaskScheduler.Current);
        }

        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return Array.TrueForAll(results, static b => b);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
