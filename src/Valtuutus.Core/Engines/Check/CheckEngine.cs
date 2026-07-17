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
        private ConcurrentDictionary<CheckMemoKey, Task<bool>>? _cache;

        // Double-checked lazy init: concurrent losers' `fresh` dictionaries are discarded harmlessly;
        // CompareExchange's fence makes the winner's dictionary safely visible to every reader.
        private ConcurrentDictionary<CheckMemoKey, Task<bool>> Cache
        {
            get
            {
                var existing = _cache;
                if (existing is not null) return existing;
                var fresh = new ConcurrentDictionary<CheckMemoKey, Task<bool>>(concurrencyLevel: 1, capacity: 4);
                return Interlocked.CompareExchange(ref _cache, fresh, null) ?? fresh;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(CheckMemoKey key, [MaybeNullWhen(false)] out Task<bool> task)
        {
            var cache = _cache;
            if (cache is null) { task = null; return false; }
            return cache.TryGetValue(key, out task);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Task<bool> GetOrAdd(CheckMemoKey key, Task<bool> task)
            => Cache.GetOrAdd(key, task);

        public Task<bool> GetOrAdd(CheckMemoKey key, Func<Task<bool>> factory, out bool added)
        {
            Task<bool>? factoryResult = null;
            var task = Cache.GetOrAdd(key, _ => { factoryResult = factory(); return factoryResult; });
            added = factoryResult is not null && ReferenceEquals(task, factoryResult);
            return task;
        }
    }

    //<inheritdoc/>
    public async Task<bool> Check(CheckRequest req, CancellationToken cancellationToken)
    {
        // Skip the tags iterator allocation entirely when nothing is listening.
        using var activity = DefaultActivitySource.Instance.HasListeners()
            ? DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal, tags: CreateCheckSpanAttributes(req))
            : null;

        ValtuutusMetrics.CheckRequests.Add(1);
        var snapToken = await SnapTokenUtils.ResolveLatest(reader, req.SnapToken, cancellationToken);
        var ctx = new CheckRequestContext
        {
            SubjectType = req.SubjectType,
            SubjectId = req.SubjectId,
            SnapToken = snapToken,
            Context = req.Context
        };
        var val = await CheckInternal(ctx, req.EntityType, req.EntityId, req.Permission, req.SubjectRelation,
            req.Depth, new CheckMemo(), cancellationToken, memoize: false);
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
        // Skip the tags iterator allocation entirely when nothing is listening.
        using var activity = DefaultActivitySource.Instance.HasListeners()
            ? DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
                tags: CreateSubjectPermissionSpanAttributes(req))
            : null;
        ValtuutusMetrics.CheckRequests.Add(1);
        var permissions = schema.GetPermissions(req.EntityType);
        var snapToken = await SnapTokenUtils.ResolveLatest(reader, req.SnapToken, cancellationToken);

        var count = permissions.Count;
        var names = new string[count];
        var tasks = new Task<bool>[count];
        var memo = new CheckMemo();
        var ctx = new CheckRequestContext
        {
            SubjectType = req.SubjectType,
            SubjectId = req.SubjectId,
            SnapToken = snapToken,
            Context = new Dictionary<string, object>(0)
        };

        var i = 0;
        foreach (var perm in permissions)
        {
            names[i] = perm.Name;
            tasks[i] = CheckInternal(ctx, req.EntityType, req.EntityId, perm.Name, null, req.Depth, memo,
                cancellationToken);
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
        // Skip the tags iterator allocation entirely when nothing is listening.
        using var activity = DefaultActivitySource.Instance.HasListeners()
            ? DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
                tags: CreateCheckSpanAttributes(req))
            : null;

        var snapToken = await SnapTokenUtils.ResolveLatest(reader, req.SnapToken, cancellationToken);
        var ctx = new CheckRequestContext
        {
            SubjectType = req.SubjectType,
            SubjectId = req.SubjectId,
            SnapToken = snapToken,
            Context = req.Context
        };
        var root = new CheckNode { Type = CheckNodeType.Permission, Name = req.Permission, EntityType = req.EntityType, EntityId = req.EntityId, SubjectType = req.SubjectType, SubjectId = req.SubjectId };
        var result = await CheckInternal(ctx, req.EntityType, req.EntityId, req.Permission, req.SubjectRelation,
            req.Depth, new CheckMemo(), root, cancellationToken);
        root.Result = result;
        FlattenExpressionTree(root);
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

    private Task<bool> CheckInternal(CheckRequestContext ctx, string entityType, string entityId, string permission,
        string? subjectRelation, int depth, CheckMemo memo, CancellationToken ct, bool memoize = true)
        => CheckInternal(ctx, entityType, entityId, permission, subjectRelation, depth, memo, null, ct, memoize);

    private Task<bool> CheckInternal(CheckRequestContext ctx, string entityType, string entityId, string permission,
        string? subjectRelation, int depth, CheckMemo memo, CheckNode? node, CancellationToken ct, bool memoize = true)
    {
        if (depth <= 0)
        {
            if (node is not null) node.Detail = "depth limit reached";
            return Task.FromResult(false);
        }

        if (!string.IsNullOrEmpty(subjectRelation)
            && ctx.SubjectType == entityType
            && ctx.SubjectId == entityId
            && subjectRelation == permission)
            return Task.FromResult(true);

        if (!string.IsNullOrEmpty(ctx.SubjectType)
            && !schema.CanSubjectTypeReach(entityType, permission, ctx.SubjectType))
        {
            if (node is not null) node.Detail = "subject type cannot reach permission";
            return Task.FromResult(false);
        }

        var nextDepth = depth - 1;

        var key = new CheckMemoKey(entityType, entityId, permission, ctx.SubjectType, ctx.SubjectId);

        if (node is null)
        {
            if (memo.TryGet(key, out var cached))
            {
                ValtuutusMetrics.MemoHits.Add(1);
                return cached!;
            }
            var task = schema.GetRelationType(entityType, permission) switch
            {
                RelationType.DirectRelation => CheckRelation(ctx, entityType, entityId, permission, nextDepth, memo, null, ct),
                RelationType.Permission => CheckPermission(ctx, entityType, entityId, permission, subjectRelation,
                    schema.GetPermission(entityType, permission), nextDepth, memo, null, ct),
                RelationType.Attribute => CheckAttribute(ctx, entityType, entityId, permission, null, ct),
                _ => Task.FromResult(false)
            };
            return memoize ? memo.GetOrAdd(key, task) : task;
        }
        else
        {
            var memoTask = memo.GetOrAdd(key, () => schema.GetRelationType(entityType, permission) switch
            {
                RelationType.DirectRelation => CheckRelation(ctx, entityType, entityId, permission, nextDepth, memo, node, ct),
                RelationType.Permission => CheckPermission(ctx, entityType, entityId, permission, subjectRelation,
                    schema.GetPermission(entityType, permission), nextDepth, memo, node, ct),
                RelationType.Attribute => CheckAttribute(ctx, entityType, entityId, permission, node, ct),
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

    private Task<bool> CheckPermission(CheckRequestContext ctx, string entityType, string entityId, string permissionName,
        string? subjectRelation, Permission permission, int depth, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity("CheckPermission");
        var permissionNode = permission!.Tree;
        return permissionNode.Type == PermissionNodeType.Expression
            ? CheckExpression(ctx, entityType, entityId, permissionName, subjectRelation, depth, permissionNode, memo, node, ct)
            : CheckLeaf(ctx, entityType, entityId, permissionName, subjectRelation, depth, permissionNode, memo, node, ct);
    }

    private async Task<bool> CheckAttribute(CheckRequestContext ctx, string entityType, string entityId,
        string permission, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        if (node is null)
            return await reader.HasTrueBoolAttribute(entityType, entityId, permission, ctx.SnapToken, ct);

        node.Type = CheckNodeType.Attribute;

        var attribute = await reader.GetAttribute(
            new EntityAttributeFilter
            {
                Attribute = permission,
                EntityId = entityId,
                EntityType = entityType,
                SnapToken = ctx.SnapToken
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

    private Task<bool> CheckExpression(CheckRequestContext ctx, string entityType, string entityId, string permission,
        string? subjectRelation, int depth, PermissionNode permNode, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return permNode.ExpressionNode!.Operation switch
        {
            PermissionOperation.Intersect => CheckExpressionWithWrapper(ctx, entityType, entityId, permission, subjectRelation, depth,
                permNode, memo, node, "and", isUnion: false, ct),
            PermissionOperation.Union => CheckExpressionWithWrapper(ctx, entityType, entityId, permission, subjectRelation, depth,
                permNode, memo, node, "or", isUnion: true, ct),
            PermissionOperation.Negate => NegateCheck(ctx, entityType, entityId, permission, subjectRelation, depth,
                permNode.ExpressionNode!.Children[0], memo, node, ct),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<bool> CheckExpressionWithWrapper(CheckRequestContext ctx, string entityType, string entityId,
        string permission, string? subjectRelation, int depth, PermissionNode permNode, CheckMemo memo, CheckNode? node, string opName,
        bool isUnion, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        CheckNode? exprNode;
        bool owned;
        if (node is not null && node.Type == CheckNodeType.Expression)
        {
            // Reuse the pre-allocated expression node from the parent's CheckExpressionChild call.
            // The parent will add it to its own parent after WhenAll, so we must not double-add.
            exprNode = node;
            owned = false;
        }
        else if (node is not null)
        {
            exprNode = new CheckNode { Type = CheckNodeType.Expression, Name = opName, EntityType = entityType, EntityId = entityId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };
            owned = true;
        }
        else
        {
            exprNode = null;
            owned = false;
        }
        var result = await CheckExpressionChild(ctx, entityType, entityId, permission, subjectRelation, depth,
            permNode.ExpressionNode!.Children, memo, exprNode, isUnion, ct);
        if (exprNode is not null)
        {
            exprNode.Result = result;
            if (owned) node!._children.Add(exprNode);
        }
        return result;
    }

    private async Task<bool> NegateCheck(CheckRequestContext ctx, string entityType, string entityId, string permission,
        string? subjectRelation, int depth, PermissionNode child, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        CheckNode? childNode = null;
        if (node is not null)
        {
            var (type, name) = GetNodeInfo(child);
            childNode = new CheckNode { Type = type, Name = name, EntityType = entityType, EntityId = entityId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };
        }

        var inner = child.Type == PermissionNodeType.Expression
            ? await CheckExpression(ctx, entityType, entityId, permission, subjectRelation, depth, child, memo, childNode, ct)
            : await CheckLeaf(ctx, entityType, entityId, permission, subjectRelation, depth, child, memo, childNode, ct);

        if (childNode is not null)
        {
            childNode.Result = inner;
            node!._children.Add(childNode);
        }

        return !inner;
    }

    // Direct (non-indirect) leaf-permission children whose target entity type can never
    // reach req.SubjectType (per schema-precomputed reachability) are guaranteed to evaluate
    // to false. Filtering them out here — before a Task/CheckNode/pool-slot gets spent on
    // them — is equivalent to what CheckInternal's guard would eventually do, just earlier.
    private bool IsStaticallyDeadForSubject(CheckRequestContext ctx, string entityType, PermissionNode child)
    {
        if (child.Type != PermissionNodeType.Leaf) return false;
        var leaf = child.LeafNode!;
        if (leaf.Type != PermissionNodeLeafType.Permission) return false;
        var permLeaf = leaf.PermissionNode!;
        if (permLeaf.IsIndirect) return false;
        return !schema.CanSubjectTypeReach(entityType, permLeaf.Permission, ctx.SubjectType!);
    }

    // A live Union/Intersect child that is a plain direct-relation leaf on req.EntityType, with
    // no sub-relation paths, can be resolved via HasAnyOfDirectRelations instead of its own
    // per-child HasDirectRelation round trip. Leaves with sub-relation paths still need the
    // GetIndirectRelations fan-out CheckRelation does, so they're excluded here.
    private bool IsBatchableDirectRelation(string entityType, PermissionNode child, out string relationName)
    {
        relationName = "";
        if (child.Type != PermissionNodeType.Leaf) return false;
        var leaf = child.LeafNode!;
        if (leaf.Type != PermissionNodeLeafType.Permission) return false;
        var permLeaf = leaf.PermissionNode!;
        if (permLeaf.IsIndirect) return false;
        if (schema.GetRelationType(entityType, permLeaf.Permission) != RelationType.DirectRelation) return false;
        if (schema.GetRelation(entityType, permLeaf.Permission).HasSubRelationPaths) return false;
        relationName = permLeaf.Permission;
        return true;
    }

    // Resolves a single Union/Intersect child, short-circuiting through CheckMemo or a shared
    // sibling-relation batch when possible instead of the normal CheckExpression/CheckLeaf
    // dispatch chain.
    private Task<bool> ResolveChild(CheckRequestContext ctx, string entityType, string entityId, string permission,
        string? subjectRelation, int depth, CheckMemo memo, Task<HashSet<string>>? batchTask, PermissionNode child, CheckNode? childNode,
        CancellationToken ct)
    {
        if (IsBatchableDirectRelation(entityType, child, out var relationName))
        {
            var key = new CheckMemoKey(entityType, entityId, relationName, ctx.SubjectType, ctx.SubjectId);
            if (memo.TryGet(key, out var cached))
            {
                if (childNode is not null) childNode.Detail = "memoized";
                return cached;
            }
            if (batchTask is not null)
                return ResolveBatchedRelation(memo, key, batchTask, relationName, childNode);
        }

        return child.Type == PermissionNodeType.Expression
            ? CheckExpression(ctx, entityType, entityId, permission, subjectRelation, depth, child, memo, childNode, ct)
            : CheckLeaf(ctx, entityType, entityId, permission, subjectRelation, depth, child, memo, childNode, ct);
    }

    private static async Task<bool> ResolveBatchedRelation(CheckMemo memo, CheckMemoKey key,
        Task<HashSet<string>> batchTask, string relationName, CheckNode? childNode)
    {
        var matched = await batchTask.ConfigureAwait(false);
        var result = matched.Contains(relationName);
        var ourTask = Task.FromResult(result);
        // GetOrAdd, not a blind write: a concurrent sibling branch elsewhere in the tree can
        // independently resolve the same relation while this batch is in flight — whichever
        // wins the race is authoritative, and everyone awaits that same Task.
        var finalTask = memo.GetOrAdd(key, ourTask);
        if (childNode is not null)
            childNode.Detail = ReferenceEquals(finalTask, ourTask)
                ? (result ? "batched: direct tuple" : "batched: no matching tuple")
                : "memoized";
        return await finalTask.ConfigureAwait(false);
    }

    private async Task<bool> CheckExpressionChild(CheckRequestContext ctx, string entityType, string entityId,
        string permission, string? subjectRelation, int depth, List<PermissionNode> children, CheckMemo memo, CheckNode? node, bool isUnion,
        CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var totalCount = children.Count;
        if (totalCount == 0) return !isUnion;

        var subjectTypeKnown = !string.IsNullOrEmpty(ctx.SubjectType);

        if (subjectTypeKnown && !isUnion)
        {
            // Intersect: a single statically-unreachable child makes the whole node false —
            // short-circuit without spawning a task for it or any sibling.
            for (var i = 0; i < totalCount; i++)
            {
                if (!IsStaticallyDeadForSubject(ctx, entityType, children[i])) continue;
                if (node is not null)
                {
                    for (var j = 0; j < totalCount; j++)
                    {
                        var (type, name) = GetNodeInfo(children[j]);
                        node._children.Add(new CheckNode
                        {
                            Type = type, Name = name, EntityType = entityType, EntityId = entityId,
                            SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId, Result = false,
                            Detail = "subject type cannot reach permission"
                        });
                    }
                }
                return false;
            }
        }

        // Union: drop statically-dead children before spawning anything for them — they can
        // only contribute `false`. Pruned nodes are recorded in explain mode up front, so in
        // that mode they appear before the live children below rather than in schema order.
        var live = children;
        if (subjectTypeKnown && isUnion)
        {
            List<PermissionNode>? filtered = null;
            for (var i = 0; i < totalCount; i++)
            {
                if (!IsStaticallyDeadForSubject(ctx, entityType, children[i]))
                {
                    filtered?.Add(children[i]);
                    continue;
                }

                if (filtered is null)
                {
                    filtered = new List<PermissionNode>(totalCount);
                    for (var j = 0; j < i; j++) filtered.Add(children[j]);
                }

                if (node is not null)
                {
                    var (type, name) = GetNodeInfo(children[i]);
                    node._children.Add(new CheckNode
                    {
                        Type = type, Name = name, EntityType = entityType, EntityId = entityId,
                        SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId, Result = false,
                        Detail = "subject type cannot reach permission"
                    });
                }
            }
            if (filtered is not null) live = filtered;
        }

        var count = live.Count;
        if (count == 0) return !isUnion;

        if (count == 1)
        {
            var only = live[0];
            CheckNode? childNode = null;
            if (node is not null)
            {
                var (type, name) = GetNodeInfo(only);
                childNode = new CheckNode { Type = type, Name = name, EntityType = entityType, EntityId = entityId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };
            }
            var result = await (only.Type == PermissionNodeType.Expression
                ? CheckExpression(ctx, entityType, entityId, permission, subjectRelation, depth, only, memo, childNode, ct)
                : CheckLeaf(ctx, entityType, entityId, permission, subjectRelation, depth, only, memo, childNode, ct));
            if (childNode is not null)
            {
                childNode.Result = result;
                node!._children.Add(childNode);
            }
            return result;
        }

        // Batch sibling direct-relation leaves on req.EntityType into a single provider call
        // instead of N separate HasDirectRelation round trips. "Check-then-batch": relations
        // already resolved or in-flight in the memo are excluded from the batch and reused
        // directly in the dispatch loop below, so this only fires for genuinely new work.
        Task<HashSet<string>>? batchTask = null;
        if (subjectTypeKnown)
        {
            List<string>? toFetch = null;
            for (var i = 0; i < count; i++)
            {
                if (!IsBatchableDirectRelation(entityType, live[i], out var relationName)) continue;
                var key = new CheckMemoKey(entityType, entityId, relationName, ctx.SubjectType, ctx.SubjectId);
                if (memo.TryGet(key, out _)) continue;
                (toFetch ??= new List<string>()).Add(relationName);
            }
            if (toFetch is { Count: >= 2 })
                batchTask = reader.HasAnyOfDirectRelations(entityType, entityId, toFetch.ToArray(),
                    ctx.SubjectId!, ctx.SnapToken, ct);
        }

        CheckNode[]? childNodes = null;
        if (node is not null)
        {
            childNodes = new CheckNode[count];
            for (var i = 0; i < count; i++)
            {
                var (type, name) = GetNodeInfo(live[i]);
                childNodes[i] = new CheckNode { Type = type, Name = name, EntityType = entityType, EntityId = entityId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };
            }
        }

        ValtuutusMetrics.ExpressionNodes.Add(1);

        var rawTasks = ArrayPool<Task<bool>>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
                rawTasks[i] = ResolveChild(ctx, entityType, entityId, permission, subjectRelation, depth, memo, batchTask, live[i],
                    childNodes?[i], ct);

            var decided = await BoolTaskCombinator.AnyOrAll(rawTasks, count, shortCircuitOn: isUnion)
                .ConfigureAwait(false);

            if (rawTasks[0].IsCompletedSuccessfully && rawTasks[0].Result == isUnion)
                ValtuutusMetrics.FirstChildDecided.Add(1);

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
            return decided;
        }
        finally
        {
            ArrayPool<Task<bool>>.Shared.Return(rawTasks, clearArray: true);
        }
    }

    private Task<bool> CheckLeaf(CheckRequestContext ctx, string entityType, string entityId, string permission,
        string? subjectRelation, int depth, PermissionNode permNode, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        return permNode.LeafNode!.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(ctx, entityType, entityId, permission, subjectRelation, depth,
                permNode.LeafNode!.PermissionNode!, memo, node, ct),
            PermissionNodeLeafType.Expression => CheckLeafFn(ctx, entityType, entityId, permNode.LeafNode!.ExpressionNode!,
                node, ct),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<bool> CheckLeafFn(CheckRequestContext ctx, string entityType, string entityId,
        PermissionNodeLeafExp leafExp, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        if (node is not null) node.Type = CheckNodeType.Function;

        var fn = schema.Functions[leafExp.FunctionName];

        if (fn is null)
            throw new InvalidOperationException();

        if (!leafExp.IsContextValid(ctx.Context))
        {
            if (node is not null) node.Detail = "fn result=False (invalid context)";
            return false;
        }

        var attributeArguments = leafExp.AttributeArgNames;

        using var attributes = await reader.GetAttributesSingleEntity(
            new EntityAttributesFilter
            {
                Attributes = attributeArguments,
                EntityId = entityId,
                EntityType = entityType,
                SnapToken = ctx.SnapToken
            }, ct);

        // Attribute order/completeness from the reader isn't guaranteed across providers
        // (a WHERE attribute IN (...) filter can return fewer rows than requested, in any
        // order), so index-matching against attributeArguments isn't safe. Build a lookup
        // once instead of rescanning `attributes` per function parameter.
        var attributesByName = DictionaryPool<string, AttributeTuple>.Rent();
        try
        {
            foreach (var a in attributes)
                attributesByName[a.Attribute] = a;

            using var paramToArg = fn.CreateParamToArgMap(leafExp.Args);

            using var fnArgs = paramToArg.ToLambdaArgs(
                static (arg, state) =>
                {
                    var (byName, entityType, sch) = state;
                    return byName.TryGetValue(arg.AttributeName, out var a)
                        ? a.GetValue(sch.GetAttribute(entityType, arg.AttributeName).Type)
                        : null;
                },
                (attributesByName, entityType, schema),
                ctx.Context);

            var result = fn.Lambda(fnArgs.Dictionary);
            if (node is not null) node.Detail = $"fn result={result}";
            return result;
        }
        finally
        {
            DictionaryPool<string, AttributeTuple>.Return(attributesByName);
        }
    }

    private Task<bool> CheckLeafPermission(CheckRequestContext ctx, string entityType, string entityId,
        string permission, string? subjectRelation, int depth, PermissionNodeLeafPermission leafPerm, CheckMemo memo, CheckNode? node,
        CancellationToken ct)
    {
        if (leafPerm.IsIndirect)
        {
            if (node is not null) node.Type = CheckNodeType.TupleToUserSet;
            return CheckTupleToUserSet(ctx, entityType, entityId, permission, depth, leafPerm.UserSet!,
                leafPerm.ComputedUserSet!, memo, node, ct);
        }
        return CheckComputedUserSet(ctx, entityType, entityId, leafPerm.Permission, subjectRelation, depth, memo, node, ct);
    }

    private async Task<bool> CheckComputedUserSet(CheckRequestContext ctx, string entityType, string entityId,
        string computedUserSetRelation, string? subjectRelation, int depth, CheckMemo memo, CheckNode? parentNode,
        CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        // If parentNode already represents this relation (same name — created by GetNodeInfo in CheckExpressionChild
        // or by the caller in CheckTupleToUserSet), pass it directly to avoid a duplicate node.
        // Hot path (parentNode is null) is also handled here with no allocations.
        if (parentNode is null || parentNode.Name == computedUserSetRelation)
            return await CheckInternal(ctx, entityType, entityId, computedUserSetRelation, subjectRelation, depth, memo,
                parentNode, ct);

        // Node represents a different permission (e.g. "delete" resolving to "owner") — create a child.
        var childNode = new CheckNode { Type = CheckNodeType.Permission, Name = computedUserSetRelation, EntityType = entityType, EntityId = entityId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };
        var result = await CheckInternal(ctx, entityType, entityId, computedUserSetRelation, subjectRelation, depth, memo,
            childNode, ct);
        childNode.Result = result;
        parentNode._children.Add(childNode);
        return result;
    }

    private async Task<bool> CheckTupleToUserSet(CheckRequestContext ctx, string entityType, string entityId,
        string permission, int depth, string tupleSetRelation, string computedUserSetRelation, CheckMemo memo,
        CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var tupleSetRelationSchema = schema.GetRelation(entityType, tupleSetRelation);
        if (tupleSetRelationSchema.Entities.Count == 1
            && tupleSetRelationSchema.Entities[0].Relation is null
            && depth > 0
            && !string.IsNullOrEmpty(ctx.SubjectType))
        {
            var subEntityType = tupleSetRelationSchema.Entities[0].Type;
            if (schema.GetRelationType(subEntityType, computedUserSetRelation) == RelationType.DirectRelation)
            {
                var computedRel = schema.GetRelation(subEntityType, computedUserSetRelation);
                if (!computedRel.HasSubRelationPaths && computedRel.EntityTypes.Contains(ctx.SubjectType))
                {
                    var fastResult = await reader.HasTupleToUserSetRelation(
                        entityType, entityId,
                        tupleSetRelation,
                        subEntityType, computedUserSetRelation,
                        ctx.SubjectType!, ctx.SubjectId!,
                        ctx.SnapToken, ct);
                    if (node is not null) node.Detail = fastResult ? "fast-path: direct join found" : "fast-path: no join found";
                    return fastResult;
                }
            }
        }

        using var relations = await reader.GetRelations(
            new RelationTupleFilter
            {
                EntityId = entityId,
                EntityType = entityType,
                Relation = tupleSetRelation,
                SnapToken = ctx.SnapToken
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
                : new CheckNode { Type = CheckNodeType.Permission, Name = computedUserSetRelation, EntityType = only.SubjectType, EntityId = only.SubjectId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };

            var singleResult = await CheckComputedUserSet(ctx, only.SubjectType, only.SubjectId,
                computedUserSetRelation, only.SubjectRelation, depth, memo, childNode, ct);

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
                    ctx.SubjectId!, ctx.SnapToken, ct);
                if (node is not null) node.Detail = batchResult ? "batch: direct relation found" : "batch: no direct relation";
                return batchResult;
            }
            finally
            {
                Array.Clear(entityIds, 0, relations.Count);
                ArrayPool<string>.Shared.Return(entityIds);
            }
        }

        CheckNode[]? childNodes = node is null ? null : new CheckNode[relations.Count];
        if (childNodes is not null)
            for (var i = 0; i < relations.Count; i++)
                childNodes[i] = new CheckNode { Type = CheckNodeType.Permission, Name = computedUserSetRelation, EntityType = relations[i].SubjectType, EntityId = relations[i].SubjectId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };

        var taskCount = relations.Count;
        var tasks = ArrayPool<Task<bool>>.Shared.Rent(taskCount);
        try
        {
            for (var i = 0; i < taskCount; i++)
            {
                var relation = relations[i];
                tasks[i] = CheckComputedUserSet(ctx, relation.SubjectType, relation.SubjectId,
                    computedUserSetRelation, relation.SubjectRelation, depth, memo, childNodes?[i], ct);
            }

            var decided = await BoolTaskCombinator.AnyOrAll(tasks, taskCount, shortCircuitOn: true)
                .ConfigureAwait(false);

            if (childNodes is not null)
                for (var i = 0; i < taskCount; i++)
                {
                    if (tasks[i].IsCompletedSuccessfully)
                        childNodes[i].Result = tasks[i].Result;
                    node!._children.Add(childNodes[i]);
                }
            return decided;
        }
        finally
        {
            ArrayPool<Task<bool>>.Shared.Return(tasks, clearArray: true);
        }
    }

    private async Task<bool> CheckRelation(CheckRequestContext ctx, string entityType, string entityId,
        string permission, int depth, CheckMemo memo, CheckNode? node, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        if (node is not null) node.Type = CheckNodeType.Relation;

        var filter = new RelationTupleFilter
        {
            EntityId = entityId,
            EntityType = entityType,
            Relation = permission,
            SnapToken = ctx.SnapToken
        };

        var hasDirect = await reader.HasDirectRelation(filter, ctx.SubjectId!, ct);
        if (hasDirect)
        {
            if (node is not null) node.Detail = "direct tuple";
            return true;
        }

        if (!schema.GetRelation(entityType, permission).HasSubRelationPaths)
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
                : new CheckNode { Type = CheckNodeType.Permission, Name = only.SubjectRelation ?? only.SubjectType, EntityType = only.SubjectType, EntityId = only.SubjectId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };

            var singleResult = await CheckInternal(ctx, only.SubjectType, only.SubjectId, only.SubjectRelation!, null,
                depth, memo, childNode, ct);

            if (childNode is not null)
            {
                childNode.Result = singleResult;
                node!._children.Add(childNode);
            }
            return singleResult;
        }

        CheckNode[]? childNodes = node is null ? null : new CheckNode[indirectRelations.Count];
        if (childNodes is not null)
            for (var i = 0; i < indirectRelations.Count; i++)
            {
                ref readonly var r = ref indirectRelations.AsSpan()[i];
                childNodes[i] = new CheckNode { Type = CheckNodeType.Permission, Name = r.SubjectRelation ?? r.SubjectType, EntityType = r.SubjectType, EntityId = r.SubjectId, SubjectType = ctx.SubjectType, SubjectId = ctx.SubjectId };
            }

        var taskCount = indirectRelations.Count;
        var tasks = ArrayPool<Task<bool>>.Shared.Rent(taskCount);
        try
        {
            var count = 0;
            foreach (ref readonly var relation in indirectRelations.AsSpan())
            {
                tasks[count] = CheckInternal(ctx, relation.SubjectType, relation.SubjectId, relation.SubjectRelation!,
                    null, depth, memo, childNodes?[count], ct);
                count++;
            }

            var decided = await BoolTaskCombinator.AnyOrAll(tasks, taskCount, shortCircuitOn: true)
                .ConfigureAwait(false);

            if (childNodes is not null)
                for (var i = 0; i < taskCount; i++)
                {
                    if (tasks[i].IsCompletedSuccessfully)
                        childNodes[i].Result = tasks[i].Result;
                    node!._children.Add(childNodes[i]);
                }
            return decided;
        }
        finally
        {
            ArrayPool<Task<bool>>.Shared.Return(tasks, clearArray: true);
        }
    }

    private static (CheckNodeType type, string name) GetNodeInfo(PermissionNode child)
    {
        if (child.Type == PermissionNodeType.Expression)
        {
            var opName = child.ExpressionNode!.Operation switch
            {
                PermissionOperation.Union => "or",
                PermissionOperation.Intersect => "and",
                PermissionOperation.Negate => "not",
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

    // Flatten consecutive same-named Expression nodes so e.g. "a or b or c" (parsed as a
    // left-associative binary tree) renders as one OR node with three children instead of two nested ORs.
    private static void FlattenExpressionTree(CheckNode node)
    {
        foreach (var child in node.Children)
            FlattenExpressionTree(child);

        if (node.Type != CheckNodeType.Expression) return;

        bool changed = false;
        for (var i = 0; i < node._children.Count; i++)
        {
            if (node._children[i].Type == CheckNodeType.Expression && node._children[i].Name == node.Name)
            { changed = true; break; }
        }
        if (!changed) return;

        var flat = new List<CheckNode>(node._children.Count + 2);
        foreach (var child in node._children)
        {
            if (child.Type == CheckNodeType.Expression && child.Name == node.Name)
                flat.AddRange(child._children);
            else
                flat.Add(child);
        }
        node._children.Clear();
        node._children.AddRange(flat);
    }

    private static bool AllSameSubjectType(ReadOnlySpan<RelationTuple> relations, string expectedType)
    {
        foreach (ref readonly var r in relations)
            if (r.SubjectType != expectedType) return false;
        return true;
    }
}
