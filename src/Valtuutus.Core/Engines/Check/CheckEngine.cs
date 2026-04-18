using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
    private readonly struct CheckMemoKey : IEquatable<CheckMemoKey>
    {
        public readonly string EntityType;
        public readonly string EntityId;
        public readonly string Permission;
        public readonly string? SubjectType;
        public readonly string? SubjectId;

        public CheckMemoKey(string entityType, string entityId, string permission, string? subjectType, string? subjectId)
        {
            EntityType = entityType;
            EntityId = entityId;
            Permission = permission;
            SubjectType = subjectType;
            SubjectId = subjectId;
        }

        public bool Equals(CheckMemoKey other) =>
            EntityType == other.EntityType && EntityId == other.EntityId &&
            Permission == other.Permission && SubjectType == other.SubjectType &&
            SubjectId == other.SubjectId;

        public override bool Equals(object? obj) => obj is CheckMemoKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(EntityType, EntityId, Permission, SubjectType, SubjectId);
    }

    private sealed class CheckMemo
    {
        private readonly ConcurrentDictionary<CheckMemoKey, Task<bool>> _cache = new(concurrencyLevel: 1, capacity: 4);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(CheckMemoKey key, [MaybeNullWhen(false)] out Task<bool> task)
            => _cache.TryGetValue(key, out task);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Task<bool> GetOrAdd(CheckMemoKey key, Task<bool> task)
            => _cache.GetOrAdd(key, task);

        public Task<bool> GetOrAdd(CheckMemoKey key, Func<Task<bool>> factory, out bool added)
        {
            Task<bool>? factoryResult = null;
            var task = _cache.GetOrAdd(key, _ => { factoryResult = factory(); return factoryResult; });
            added = factoryResult is not null && ReferenceEquals(task, factoryResult);
            return task;
        }
    }

    //<inheritdoc/>
    public async Task<bool> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        using var activity =
            DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateCheckSpanAttributes(req));

        await SnapTokenUtils.LoadLatestSnapToken(reader, req, cancellationToken);
        var val = await CheckInternal(req, new CheckMemo(), cancellationToken);
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
        var memo = new CheckMemo();

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
            }, memo, cancellationToken);
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

    //<inheritdoc/>
    public async Task<CheckExplainResult> Explain(CheckRequest req, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
            tags: CreateCheckSpanAttributes(req));

        await SnapTokenUtils.LoadLatestSnapToken(reader, req, cancellationToken);
        var root = new CheckNode { Type = CheckNodeType.Permission, Name = req.Permission };
        var result = await CheckInternal(req, new CheckMemo(), root, cancellationToken);
        root.Result = result;
        activity?.AddEvent(new ActivityEvent("ExplainFinished",
            tags: new ActivityTagsCollection(CreateCheckResultAttributes(result))));
        return new CheckExplainResult { Result = result, Root = root };
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

    private Task<bool> CheckInternal(CheckRequest req, CheckMemo memo, CancellationToken ct)
        => CheckInternal(req, memo, null, ct);

    private Task<bool> CheckInternal(CheckRequest req, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        if (req.CheckDepthLimit())
        {
            if (node is not null) node.Detail = "depth limit reached";
            return Task.FromResult(false);
        }

        if (!string.IsNullOrEmpty(req.SubjectRelation)
            && req.SubjectType == req.EntityType
            && req.SubjectId == req.EntityId
            && req.SubjectRelation == req.Permission)
            return Task.FromResult(true);

        if (!string.IsNullOrEmpty(req.SubjectType)
            && !schema.CanSubjectTypeReach(req.EntityType, req.Permission, req.SubjectType))
        {
            if (node is not null) node.Detail = "subject type cannot reach permission";
            return Task.FromResult(false);
        }

        req.DecreaseDepth();

        var key = new CheckMemoKey(req.EntityType, req.EntityId, req.Permission, req.SubjectType, req.SubjectId);

        if (node is null)
        {
            if (memo.TryGet(key, out var cached)) return cached!;
            var task = schema.GetRelationType(req.EntityType, req.Permission) switch
            {
                RelationType.DirectRelation => CheckRelation(req, memo, null, ct),
                RelationType.Permission => CheckPermission(req, schema.GetPermission(req.EntityType, req.Permission), memo, null, ct),
                RelationType.Attribute => CheckAttribute(req, null, ct),
                _ => Task.FromResult(false)
            };
            return memo.GetOrAdd(key, task);
        }
        else
        {
            var memoTask = memo.GetOrAdd(key, () => schema.GetRelationType(req.EntityType, req.Permission) switch
            {
                RelationType.DirectRelation => CheckRelation(req, memo, node, ct),
                RelationType.Permission => CheckPermission(req, schema.GetPermission(req.EntityType, req.Permission), memo, node, ct),
                RelationType.Attribute => CheckAttribute(req, node, ct),
                _ => Task.FromResult(false)
            }, out bool added);

            if (!added)
            {
                node.Type = CheckNodeType.Permission;
                node.Detail = "memoized";
                return memoTask.ContinueWith(
                    static (t, s) => { ((CheckNode)s!).Result = t.Result; return t.Result; },
                    node, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);
            }

            return memoTask;
        }
    }

    private Task<bool> CheckPermission(CheckRequest req, Permission permission, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity("CheckPermission");
        var permissionNode = permission!.Tree;
        return permissionNode.Type == PermissionNodeType.Expression
            ? CheckExpression(req, permissionNode, memo, node, ct)
            : CheckLeaf(req, permissionNode, memo, node, ct);
    }

    private async Task<bool> CheckAttribute(CheckRequest req, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        if (node is not null) node.Type = CheckNodeType.Attribute;

        var attribute = await reader.GetAttribute(
            new EntityAttributeFilter
            {
                Attribute = req.Permission,
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                SnapToken = req.SnapToken ?? SnapToken.MinValue
            }, ct);

        if (attribute is null)
        {
            if (node is not null) node.Detail = "attribute=False";
            return false;
        }

        var val = attribute.Value.GetValue<bool>();
        if (node is not null) node.Detail = $"attribute={val}";
        return val;
    }

    private Task<bool> CheckExpression(CheckRequest req, PermissionNode permNode, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return permNode.ExpressionNode!.Operation switch
        {
            PermissionOperation.Intersect => CheckExpressionChild(req, permNode.ExpressionNode!.Children, memo, node, isUnion: false, ct),
            PermissionOperation.Union => CheckExpressionChild(req, permNode.ExpressionNode!.Children, memo, node, isUnion: true, ct),
            PermissionOperation.Negate => NegateCheck(req, permNode.ExpressionNode!.Children[0], memo, node, ct),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<bool> NegateCheck(CheckRequest req, PermissionNode child, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        CheckNode? childNode = null;
        if (node is not null)
        {
            var (type, name) = GetNodeInfo(child);
            childNode = new CheckNode { Type = type, Name = name };
        }

        var inner = child.Type == PermissionNodeType.Expression
            ? await CheckExpression(req, child, memo, childNode, ct)
            : await CheckLeaf(req, child, memo, childNode, ct);

        if (childNode is not null)
        {
            childNode.Result = inner;
            node!._children.Add(childNode);
        }

        return !inner;
    }

    private async Task<bool> CheckExpressionChild(CheckRequest req, List<PermissionNode> children, CheckMemo memo, CheckNode? node, bool isUnion, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var count = children.Count;
        if (count == 0) return !isUnion;

        if (count == 1)
        {
            var only = children[0];
            CheckNode? childNode = null;
            if (node is not null)
            {
                var (type, name) = GetNodeInfo(only);
                childNode = new CheckNode { Type = type, Name = name };
            }
            var result = await (only.Type == PermissionNodeType.Expression
                ? CheckExpression(req, only, memo, childNode, ct)
                : CheckLeaf(req, only, memo, childNode, ct));
            if (childNode is not null)
            {
                childNode.Result = result;
                node!._children.Add(childNode);
            }
            return result;
        }

        CheckNode[]? childNodes = null;
        if (node is not null)
        {
            childNodes = new CheckNode[count];
            for (var i = 0; i < count; i++)
            {
                var (type, name) = GetNodeInfo(children[i]);
                childNodes[i] = new CheckNode { Type = type, Name = name };
            }
        }

        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        var rawTasks = new Task<bool>[count];
        var tasks = new Task<bool>[count];
        for (var i = 0; i < count; i++)
        {
            var child = children[i];
            var childNode = childNodes?[i];
            rawTasks[i] = child.Type == PermissionNodeType.Expression
                ? CheckExpression(req, child, memo, childNode, cancellationToken)
                : CheckLeaf(req, child, memo, childNode, cancellationToken);
            tasks[i] = isUnion
                ? rawTasks[i].ContinueWith(
                    static (t, s) => { if (t.Result) ((CancellationTokenSource)s!).Cancel(); return t.Result; },
                    innerCts, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current)
                : rawTasks[i].ContinueWith(
                    static (t, s) => { if (!t.Result) ((CancellationTokenSource)s!).Cancel(); return t.Result; },
                    innerCts, cancellationToken, TaskContinuationOptions.NotOnFaulted, TaskScheduler.Current);
        }

        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            if (childNodes is not null)
                for (var i = 0; i < count; i++)
                {
                    childNodes[i].Result = results[i];
                    node!._children.Add(childNodes[i]);
                }
            return isUnion ? results.AsSpan().Contains(true) : !results.AsSpan().Contains(false);
        }
        catch (OperationCanceledException)
        {
            if (childNodes is not null)
            {
                for (var i = 0; i < count; i++)
                {
                    if (rawTasks[i].IsCompletedSuccessfully)
                        childNodes[i].Result = rawTasks[i].Result;
                    else
                        childNodes[i].Detail = isUnion
                            ? "skipped (evaluation stopped after a success)"
                            : "skipped (evaluation stopped after a failure)";
                    node!._children.Add(childNodes[i]);
                }
            }
            return isUnion;
        }
    }

    private Task<bool> CheckLeaf(CheckRequest req, PermissionNode permNode, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return permNode.LeafNode!.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, permNode.LeafNode!.PermissionNode!, memo, node, ct),
            PermissionNodeLeafType.Expression => CheckLeafFn(req, permNode.LeafNode!.ExpressionNode!, node, ct),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<bool> CheckLeafFn(CheckRequest req, PermissionNodeLeafExp leafExp, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        if (node is not null) node.Type = CheckNodeType.Function;

        var fn = schema.Functions[leafExp.FunctionName];

        if (fn is null)
            throw new InvalidOperationException();

        if (!leafExp.IsContextValid(req.Context))
        {
            if (node is not null) node.Detail = "fn result=False (invalid context)";
            return false;
        }

        var attributeArguments = leafExp.GetArgsAttributesNames();

        var attributes = await reader.GetAttributes(
            new EntityAttributesFilter
            {
                Attributes = attributeArguments,
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                SnapToken = req.SnapToken ?? SnapToken.MinValue
            }, null, ct);

        using var paramToArg = fn.CreateParamToArgMap(leafExp.Args);

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

        var result = fn.Lambda(fnArgs.Dictionary);
        if (node is not null) node.Detail = $"fn result={result}";
        return result;
    }

    private Task<bool> CheckLeafPermission(CheckRequest req, PermissionNodeLeafPermission leafPerm, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        if (leafPerm.IsIndirect)
        {
            if (node is not null) node.Type = CheckNodeType.TupleToUserSet;
            return CheckTupleToUserSet(req, leafPerm.UserSet!, leafPerm.ComputedUserSet!, memo, node, ct);
        }
        return CheckComputedUserSet(req, leafPerm.Permission, memo, node, ct);
    }

    private async Task<bool> CheckComputedUserSet(CheckRequest req, string computedUserSetRelation, CheckMemo memo, CheckNode? parentNode, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        CheckNode? childNode = parentNode is null ? null
            : new CheckNode { Type = CheckNodeType.Permission, Name = computedUserSetRelation };

        var result = await CheckInternal(req with { Permission = computedUserSetRelation }, memo, childNode, ct);

        if (childNode is not null)
        {
            childNode.Result = result;
            parentNode!._children.Add(childNode);
        }

        return result;
    }

    private async Task<bool> CheckTupleToUserSet(CheckRequest req, string tupleSetRelation,
        string computedUserSetRelation, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var tupleSetRelationSchema = schema.GetRelation(req.EntityType, tupleSetRelation);
        if (tupleSetRelationSchema.Entities.Count == 1
            && tupleSetRelationSchema.Entities[0].Relation is null
            && req.Depth > 0
            && !string.IsNullOrEmpty(req.SubjectType))
        {
            var subEntityType = tupleSetRelationSchema.Entities[0].Type;
            if (schema.GetRelationType(subEntityType, computedUserSetRelation) == RelationType.DirectRelation)
            {
                var computedRel = schema.GetRelation(subEntityType, computedUserSetRelation);
                if (!computedRel.HasSubRelationPaths && computedRel.EntityTypes.Contains(req.SubjectType))
                {
                    var fastResult = await reader.HasTupleToUserSetRelation(
                        req.EntityType, req.EntityId,
                        tupleSetRelation,
                        subEntityType, computedUserSetRelation,
                        req.SubjectType!, req.SubjectId!,
                        req.SnapToken ?? SnapToken.MinValue, ct);
                    if (node is not null) node.Detail = fastResult ? "fast-path: direct join found" : "fast-path: no join found";
                    return fastResult;
                }
            }
        }

        using var relations = await reader.GetRelations(
            new RelationTupleFilter
            {
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                Relation = tupleSetRelation,
                SnapToken = req.SnapToken ?? SnapToken.MinValue
            }, ct);

        if (relations.Count == 0)
        {
            if (node is not null) node.Detail = "no matching tuples";
            return false;
        }

        if (relations.Count == 1)
        {
            var only = relations[0];
            CheckNode? childNode = node is null ? null
                : new CheckNode { Type = CheckNodeType.Permission, Name = computedUserSetRelation };

            var singleResult = await CheckComputedUserSet(new CheckRequest
            {
                EntityType = only.SubjectType,
                EntityId = only.SubjectId,
                Permission = only.SubjectRelation,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, computedUserSetRelation, memo, childNode, ct);

            if (childNode is not null)
            {
                childNode.Result = singleResult;
                node!._children.Add(childNode);
            }
            return singleResult;
        }

        var firstSubjectType = relations.AsSpan()[0].SubjectType;
        if (AllSameSubjectType(relations.AsSpan(), firstSubjectType)
            && schema.GetRelationType(firstSubjectType, computedUserSetRelation) == RelationType.DirectRelation
            && !schema.GetRelation(firstSubjectType, computedUserSetRelation).HasSubRelationPaths)
        {
            var entityIds = ArrayPool<string>.Shared.Rent(relations.Count);
            for (var i = 0; i < relations.Count; i++)
                entityIds[i] = relations[i].SubjectId;
            Array.Clear(entityIds, relations.Count, entityIds.Length - relations.Count);
            try
            {
                var batchResult = await reader.HasAnyDirectRelation(firstSubjectType, entityIds, computedUserSetRelation,
                    req.SubjectId!, req.SnapToken ?? SnapToken.MinValue, ct);
                if (node is not null) node.Detail = batchResult ? "batch: direct relation found" : "batch: no direct relation";
                return batchResult;
            }
            finally
            {
                Array.Clear(entityIds, 0, relations.Count);
                ArrayPool<string>.Shared.Return(entityIds);
            }
        }

        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        CheckNode[]? childNodes = node is null ? null : new CheckNode[relations.Count];
        if (childNodes is not null)
            for (var i = 0; i < relations.Count; i++)
                childNodes[i] = new CheckNode { Type = CheckNodeType.Permission, Name = computedUserSetRelation };

        var tasks = new Task<bool>[relations.Count];
        for (var i = 0; i < relations.Count; i++)
        {
            var relation = relations[i];
            var childNode = childNodes?[i];
            tasks[i] = CheckComputedUserSet(new CheckRequest
            {
                EntityType = relation.SubjectType,
                EntityId = relation.SubjectId,
                Permission = relation.SubjectRelation,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, computedUserSetRelation, memo, childNode, cancellationToken)
            .ContinueWith(
                static (t, s) => { if (t.Result) ((CancellationTokenSource)s!).Cancel(); return t.Result; },
                innerCts, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current);
        }

        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            if (childNodes is not null)
                foreach (var cn in childNodes) node!._children.Add(cn);
            return results.AsSpan().Contains(true);
        }
        catch (OperationCanceledException)
        {
            if (childNodes is not null)
                foreach (var cn in childNodes) node!._children.Add(cn);
            return true;
        }
    }

    private async Task<bool> CheckRelation(CheckRequest req, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(req.SubjectType)
            && !schema.IsSubjectTypeAllowedInRelation(req.EntityType, req.Permission,
                req.SubjectType, req.SubjectRelation))
            return false;

        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        if (node is not null) node.Type = CheckNodeType.Relation;

        var filter = new RelationTupleFilter
        {
            EntityId = req.EntityId,
            EntityType = req.EntityType,
            Relation = req.Permission,
            SnapToken = req.SnapToken ?? SnapToken.MinValue
        };

        var hasDirect = await reader.HasDirectRelation(filter, req.SubjectId!, ct);
        if (hasDirect)
        {
            if (node is not null) node.Detail = "direct tuple";
            return true;
        }

        if (!schema.GetRelation(req.EntityType, req.Permission).HasSubRelationPaths)
        {
            if (node is not null) node.Detail = "no matching tuple";
            return false;
        }

        using var indirectRelations = await reader.GetIndirectRelations(filter, ct);

        if (indirectRelations.Count == 0)
        {
            if (node is not null) node.Detail = "no matching tuple";
            return false;
        }

        if (indirectRelations.Count == 1)
        {
            ref readonly var only = ref indirectRelations.AsSpan()[0];
            CheckNode? childNode = node is null ? null
                : new CheckNode { Type = CheckNodeType.Permission, Name = only.SubjectRelation ?? only.SubjectType };

            var singleResult = await CheckInternal(new CheckRequest
            {
                EntityType = only.SubjectType,
                EntityId = only.SubjectId,
                Permission = only.SubjectRelation,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, memo, childNode, ct);

            if (childNode is not null)
            {
                childNode.Result = singleResult;
                node!._children.Add(childNode);
            }
            return singleResult;
        }

        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        CheckNode[]? childNodes = node is null ? null : new CheckNode[indirectRelations.Count];
        if (childNodes is not null)
            for (var i = 0; i < indirectRelations.Count; i++)
            {
                ref readonly var r = ref indirectRelations.AsSpan()[i];
                childNodes[i] = new CheckNode { Type = CheckNodeType.Permission, Name = r.SubjectRelation ?? r.SubjectType };
            }

        var tasks = new Task<bool>[indirectRelations.Count];
        var count = 0;
        foreach (ref readonly var relation in indirectRelations.AsSpan())
        {
            var childNode = childNodes?[count];
            tasks[count++] = CheckInternal(new CheckRequest
            {
                EntityType = relation.SubjectType,
                EntityId = relation.SubjectId,
                Permission = relation.SubjectRelation,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, memo, childNode, cancellationToken)
            .ContinueWith(
                static (t, s) => { if (t.Result) ((CancellationTokenSource)s!).Cancel(); return t.Result; },
                innerCts, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current);
        }

        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            if (childNodes is not null)
                foreach (var cn in childNodes) node!._children.Add(cn);
            return results.AsSpan().Contains(true);
        }
        catch (OperationCanceledException)
        {
            if (childNodes is not null)
                foreach (var cn in childNodes) node!._children.Add(cn);
            return true;
        }
    }

    private static (CheckNodeType type, string name) GetNodeInfo(PermissionNode child)
    {
        if (child.Type == PermissionNodeType.Expression)
        {
            var opName = child.ExpressionNode!.Operation switch
            {
                PermissionOperation.Union => "OR",
                PermissionOperation.Intersect => "AND",
                PermissionOperation.Negate => "NOT",
                _ => "expression"
            };
            return (CheckNodeType.Expression, opName);
        }

        if (child.LeafNode!.Type == PermissionNodeLeafType.Expression)
            return (CheckNodeType.Function, child.LeafNode!.ExpressionNode!.FunctionName);

        var perm = child.LeafNode!.PermissionNode!;
        return perm.IsIndirect
            ? (CheckNodeType.TupleToUserSet, perm.Permission)
            : (CheckNodeType.Permission, perm.Permission);
    }

    private static bool AllSameSubjectType(ReadOnlySpan<RelationTuple> relations, string expectedType)
    {
        foreach (ref readonly var r in relations)
            if (r.SubjectType != expectedType) return false;
        return true;
    }
}
