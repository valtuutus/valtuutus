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
    {
        if (req.CheckDepthLimit())
            return Task.FromResult(false);

        if (!string.IsNullOrEmpty(req.SubjectRelation)
            && req.SubjectType == req.EntityType
            && req.SubjectId == req.EntityId
            && req.SubjectRelation == req.Permission)
            return Task.FromResult(true);

        req.DecreaseDepth();

        var key = new CheckMemoKey(req.EntityType, req.EntityId, req.Permission, req.SubjectType, req.SubjectId);
        if (memo.TryGet(key, out var cached))
            return cached;

        var task = schema.GetRelationType(req.EntityType, req.Permission) switch
        {
            RelationType.DirectRelation => CheckRelation(req, memo, ct),
            RelationType.Permission => CheckPermission(req, schema.GetPermission(req.EntityType, req.Permission), memo, ct),
            RelationType.Attribute => CheckAttribute(req, ct),
            _ => Task.FromResult(false)
        };

        return memo.GetOrAdd(key, task);
    }

    private Task<bool> CheckPermission(CheckRequest req, Permission permission, CheckMemo memo, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity("CheckPermission");

        var permissionNode = permission!.Tree;

        return permissionNode.Type == PermissionNodeType.Expression
            ? CheckExpression(req, permissionNode, memo, ct)
            : CheckLeaf(req, permissionNode, memo, ct);
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

    private Task<bool> CheckExpression(CheckRequest req, PermissionNode node, CheckMemo memo, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return node.ExpressionNode!.Operation switch
        {
            PermissionOperation.Intersect => CheckExpressionChild(req, node.ExpressionNode!.Children, memo, ct, isUnion: false),
            PermissionOperation.Union => CheckExpressionChild(req, node.ExpressionNode!.Children, memo, ct, isUnion: true),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<bool> CheckExpressionChild(CheckRequest req, List<PermissionNode> children, CheckMemo memo, CancellationToken ct, bool isUnion)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var count = children.Count;
        if (count == 0) return !isUnion;
        if (count == 1)
        {
            var only = children[0];
            return await (only.Type == PermissionNodeType.Expression
                ? CheckExpression(req, only, memo, ct)
                : CheckLeaf(req, only, memo, ct));
        }

        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        var tasks = new Task<bool>[count];
        for (var i = 0; i < count; i++)
        {
            var child = children[i];
            var rawTask = child.Type == PermissionNodeType.Expression
                ? CheckExpression(req, child, memo, cancellationToken)
                : CheckLeaf(req, child, memo, cancellationToken);
            tasks[i] = isUnion
                ? rawTask.ContinueWith(
                    static (t, s) => { if (t.Result) ((CancellationTokenSource)s!).Cancel(); return t.Result; },
                    innerCts, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current)
                : rawTask.ContinueWith(
                    static (t, s) => { if (!t.Result) ((CancellationTokenSource)s!).Cancel(); return t.Result; },
                    innerCts, cancellationToken, TaskContinuationOptions.NotOnFaulted, TaskScheduler.Current);
        }

        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return isUnion ? results.AsSpan().Contains(true) : !results.AsSpan().Contains(false);
        }
        catch (OperationCanceledException)
        {
            return isUnion;
        }
    }

    private Task<bool> CheckLeaf(CheckRequest req, PermissionNode node, CheckMemo memo, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return node.LeafNode!.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, node.LeafNode!.PermissionNode!, memo, ct),
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

    private Task<bool> CheckLeafPermission(CheckRequest req, PermissionNodeLeafPermission node, CheckMemo memo, CancellationToken ct)
    {
        if (node.IsIndirect)
            return CheckTupleToUserSet(req, node.UserSet!, node.ComputedUserSet!, memo, ct);
        return CheckComputedUserSet(req, node.Permission, memo, ct);
    }

    private Task<bool> CheckComputedUserSet(CheckRequest req, string computedUserSetRelation, CheckMemo memo, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return CheckInternal(req with { Permission = computedUserSetRelation }, memo, ct);
    }

    private async Task<bool> CheckTupleToUserSet(CheckRequest req, string tupleSetRelation,
        string computedUserSetRelation, CheckMemo memo, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        using var relations = await reader.GetRelations(
            new RelationTupleFilter
            {
                EntityId = req.EntityId,
                EntityType = req.EntityType,
                Relation = tupleSetRelation,
                SnapToken = req.SnapToken
            }, ct);

        if (relations.Count == 0) return false;
        if (relations.Count == 1)
        {
            var only = relations[0];
            return await CheckComputedUserSet(new CheckRequest
            {
                EntityType = only.SubjectType,
                EntityId = only.SubjectId,
                Permission = only.SubjectRelation,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, computedUserSetRelation, memo, ct);
        }

        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        var tasks = new Task<bool>[relations.Count];
        for (var i = 0; i < relations.Count; i++)
        {
            var relation = relations[i];
            tasks[i] = CheckComputedUserSet(new CheckRequest
            {
                EntityType = relation.SubjectType,
                EntityId = relation.SubjectId,
                Permission = relation.SubjectRelation,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, computedUserSetRelation, memo, cancellationToken)
            .ContinueWith(
                static (t, s) => { if (t.Result) ((CancellationTokenSource)s!).Cancel(); return t.Result; },
                innerCts, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current);
        }

        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.AsSpan().Contains(true);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private async Task<bool> CheckRelation(CheckRequest req, CheckMemo memo, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(req.SubjectType)
            && !schema.IsSubjectTypeAllowedInRelation(req.EntityType, req.Permission,
                req.SubjectType, req.SubjectRelation))
            return false;

        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var filter = new RelationTupleFilter
        {
            EntityId = req.EntityId,
            EntityType = req.EntityType,
            Relation = req.Permission,
            SnapToken = req.SnapToken
        };

        if (await reader.HasDirectRelation(filter, req.SubjectId, ct))
            return true;

        using var indirectRelations = await reader.GetIndirectRelations(filter, ct);

        if (indirectRelations.Count == 0) return false;
        if (indirectRelations.Count == 1)
        {
            ref readonly var only = ref indirectRelations.AsSpan()[0];
            return await CheckInternal(new CheckRequest
            {
                EntityType = only.SubjectType,
                EntityId = only.SubjectId,
                Permission = only.SubjectRelation,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, memo, ct);
        }

        using var pooledCts = CancellationTokenSourcePool.Rent(ct);
        var cancellationToken = pooledCts.Token;
        var innerCts = pooledCts.InnerSource;

        var tasks = new Task<bool>[indirectRelations.Count];
        var count = 0;
        foreach (ref readonly var relation in indirectRelations.AsSpan())
        {
            tasks[count++] = CheckInternal(new CheckRequest
            {
                EntityType = relation.SubjectType,
                EntityId = relation.SubjectId,
                Permission = relation.SubjectRelation,
                SubjectId = req.SubjectId,
                SnapToken = req.SnapToken,
                Depth = req.Depth
            }, memo, cancellationToken)
            .ContinueWith(
                static (t, s) => { if (t.Result) ((CancellationTokenSource)s!).Cancel(); return t.Result; },
                innerCts, cancellationToken, TaskContinuationOptions.NotOnCanceled, TaskScheduler.Current);
        }

        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.AsSpan().Contains(true);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}
