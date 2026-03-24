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

internal enum CheckNodeKind
{
    Fail,
    DirectRelation,
    Attribute,
    TupleToUserSet,
    LeafFn,
    ExpressionUnion,
    ExpressionIntersect
}

/// <summary>
/// Stack-friendly discriminated union representing a unit of check work.
/// Replaces heap-allocated Func&lt;CancellationToken, Task&lt;bool&gt;&gt; closures.
/// </summary>
internal readonly struct CheckNode
{
    internal readonly CheckNodeKind Kind;
    internal readonly CheckRequest Req;
    internal readonly string? Arg1;   // TupleToUserSet: tupleSetRelation
    internal readonly string? Arg2;   // TupleToUserSet: computedUserSetRelation
    internal readonly PermissionNodeLeafExp? LeafExp;
    internal readonly List<CheckNode>? Children;  // ExpressionUnion/ExpressionIntersect

    internal static readonly CheckNode Fail = new CheckNode(CheckNodeKind.Fail, default);

    internal CheckNode(CheckNodeKind kind, CheckRequest req,
        string? arg1 = null, string? arg2 = null,
        PermissionNodeLeafExp? leafExp = null,
        List<CheckNode>? children = null)
    {
        Kind = kind;
        Req = req;
        Arg1 = arg1;
        Arg2 = arg2;
        LeafExp = leafExp;
        Children = children;
    }
}

public sealed class CheckEngine(IDataReaderProvider reader, Schema schema) : ICheckEngine
{
    //<inheritdoc/>
    public async Task<bool> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        using var activity =
            DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateCheckSpanAttributes(req));

        await SnapTokenUtils.LoadLatestSnapToken(reader, req, cancellationToken);
        var val = await ExecuteNode(BuildNode(req), cancellationToken);
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
            var node = BuildNode(new CheckRequest
            {
                EntityType = req.EntityType,
                EntityId = req.EntityId,
                Permission = perm.Name,
                SubjectType = req.SubjectType,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            });
            tasks[i] = ExecuteNode(node, cancellationToken);
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

    // ── Build phase: traverses schema tree, produces CheckNode structs (no async I/O) ──

    private CheckNode BuildNode(CheckRequest req)
    {
        if (req.CheckDepthLimit())
            return CheckNode.Fail;

        req.DecreaseDepth();

        return schema.GetRelationType(req.EntityType, req.Permission) switch
        {
            RelationType.DirectRelation => new CheckNode(CheckNodeKind.DirectRelation, req),
            RelationType.Permission => BuildPermission(req, schema.GetPermission(req.EntityType, req.Permission)),
            RelationType.Attribute => new CheckNode(CheckNodeKind.Attribute, req),
            _ => CheckNode.Fail
        };
    }

    private CheckNode BuildPermission(CheckRequest req, Permission permission)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity("CheckPermission");

        var permissionNode = permission!.Tree;

        return permissionNode.Type == PermissionNodeType.Expression
            ? BuildExpression(req, permissionNode)
            : BuildLeaf(req, permissionNode);
    }

    private CheckNode BuildExpression(CheckRequest req, PermissionNode node)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var expressionNode = node.ExpressionNode!;
        var children = new List<CheckNode>(expressionNode.Children.Count);
        foreach (var child in expressionNode.Children)
        {
            children.Add(child.Type == PermissionNodeType.Expression
                ? BuildExpression(req, child)
                : BuildLeaf(req, child));
        }

        return expressionNode.Operation == PermissionOperation.Union
            ? new CheckNode(CheckNodeKind.ExpressionUnion, req, children: children)
            : new CheckNode(CheckNodeKind.ExpressionIntersect, req, children: children);
    }

    private CheckNode BuildLeaf(CheckRequest req, PermissionNode node)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return node.LeafNode!.Type switch
        {
            PermissionNodeLeafType.Permission => BuildLeafPermission(req, node.LeafNode!.PermissionNode!),
            PermissionNodeLeafType.Expression => new CheckNode(CheckNodeKind.LeafFn, req, leafExp: node.LeafNode!.ExpressionNode!),
            _ => throw new InvalidOperationException()
        };
    }

    private CheckNode BuildLeafPermission(CheckRequest req, PermissionNodeLeafPermission node)
    {
        if (node.IsIndirect)
            return new CheckNode(CheckNodeKind.TupleToUserSet, req, arg1: node.UserSet!, arg2: node.ComputedUserSet!);
        // ComputedUserSet: recurse into BuildNode with new permission
        return BuildNode(req with { Permission = node.Permission });
    }

    // ── Execute phase: dispatches on CheckNodeKind to perform async I/O ──

    private Task<bool> ExecuteNode(in CheckNode node, CancellationToken ct)
    {
        return node.Kind switch
        {
            CheckNodeKind.Fail => Task.FromResult(false),
            CheckNodeKind.DirectRelation => CheckRelation(node.Req, ct),
            CheckNodeKind.Attribute => CheckAttribute(node.Req, ct),
            CheckNodeKind.TupleToUserSet => CheckTupleToUserSet(node.Req, node.Arg1!, node.Arg2!, ct),
            CheckNodeKind.LeafFn => CheckLeafFn(node.Req, node.LeafExp!, ct),
            CheckNodeKind.ExpressionUnion => ExecuteUnion(node.Children!, ct),
            CheckNodeKind.ExpressionIntersect => ExecuteIntersect(node.Children!, ct),
            _ => Task.FromResult(false)
        };
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

        var pool = ArrayPool<CheckNode>.Shared;
        var buffer = pool.Rent(relations.Count);
        try
        {
            for (var i = 0; i < relations.Count; i++)
            {
                var relation = relations[i];
                buffer[i] = BuildNode(new CheckRequest
                {
                    EntityType = relation.SubjectType,
                    EntityId = relation.SubjectId,
                    Permission = computedUserSetRelation,
                    SubjectId = req.SubjectId,
                    SnapToken = req.SnapToken,
                    Depth = req.Depth
                });
            }
            return await ExecuteUnion(buffer, relations.Count, ct);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private async Task<bool> CheckRelation(CheckRequest req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var relations = await reader.GetRelations(
            new RelationTupleFilter
            {
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                Relation = req.Permission,
                SnapToken = req.SnapToken
            }, ct);

        if (relations.Count == 0) return false;

        var pool = ArrayPool<CheckNode>.Shared;
        var buffer = pool.Rent(relations.Count);
        var count = 0;
        try
        {
            foreach (var relation in relations)
            {
                if (relation.SubjectId == req.SubjectId)
                    return true;

                if (!relation.IsDirectSubject())
                {
                    buffer[count++] = BuildNode(new CheckRequest
                    {
                        EntityType = relation.SubjectType,
                        EntityId = relation.SubjectId,
                        Permission = relation.SubjectRelation,
                        SubjectId = req.SubjectId,
                        SnapToken = req.SnapToken,
                        Depth = req.Depth
                    });
                }
            }

            if (count == 0) return false;
            return await ExecuteUnion(buffer, count, ct);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private Task<bool> ExecuteUnion(CheckNode[] nodes, int count, CancellationToken ct)
    {
        if (count == 0) return Task.FromResult(false);
        if (count == 1) return ExecuteNode(nodes[0], ct);
        return ExecuteUnionCore(nodes, count, ct);
    }

    private Task<bool> ExecuteUnion(List<CheckNode> nodes, CancellationToken ct)
    {
        if (nodes.Count == 0) return Task.FromResult(false);
        if (nodes.Count == 1) return ExecuteNode(nodes[0], ct);
        return ExecuteUnionCore(nodes, nodes.Count, ct);
    }

    private async Task<bool> ExecuteUnionCore(IReadOnlyList<CheckNode> nodes, int count, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        var tasks = new Task<bool>[count];
        for (var i = 0; i < count; i++)
        {
            tasks[i] = ExecuteNode(nodes[i], cancellationToken).ContinueWith(
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

    private Task<bool> ExecuteIntersect(List<CheckNode> nodes, CancellationToken ct)
    {
        if (nodes.Count == 0) return Task.FromResult(true);
        if (nodes.Count == 1) return ExecuteNode(nodes[0], ct);
        return ExecuteIntersectCore(nodes, ct);
    }

    private async Task<bool> ExecuteIntersectCore(List<CheckNode> nodes, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        var tasks = new Task<bool>[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            tasks[i] = ExecuteNode(nodes[i], cancellationToken).ContinueWith(
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
