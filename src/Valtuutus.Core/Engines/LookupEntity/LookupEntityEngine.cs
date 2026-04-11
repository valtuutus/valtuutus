using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
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
    public required string[] SubjectsIds { get; init; }
    public string? SubjectRelation { get; init; }
    public required string FinalSubjectType { get; init; }
    public required string FinalSubjectId { get; init; }
    public SnapToken? SnapToken { get; set; }
    public required int Depth { get; set; } = 10;
    public required IDictionary<string, object> Context { get; set; } = EmptyContext;
    // Request-scoped cache for GetAttributes(EntityAttributesFilter) results.
    // Memoises the Task itself — concurrent callers await the same Task rather than
    // issuing duplicate DB queries for the same (entityType, attributeNames) combination.
    public required ConcurrentDictionary<string, Task<Dictionary<(string, string), AttributeTuple>>> AttributeCache { get; init; }
    public EntityScope? Scope { get; init; }
}

public sealed class LookupEntityEngine(
    Schema schema,
    IDataReaderProvider reader) : ILookupEntityEngine
{
    //<inheritdoc/>
    public async Task<LookupEntityPage> LookupEntity(LookupEntityRequest req, CancellationToken cancellationToken)
    {
        using var activity =
            DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
                tags: CreateLookupEntitySpanAttributes(req));

        // Validate scope relation exists in schema
        if (req.Scope is { } scope)
        {
            if (schema.GetRelationType(req.EntityType, scope.Relation) == RelationType.None)
                throw new InvalidOperationException(
                    $"Scope relation '{scope.Relation}' does not exist on entity type '{req.EntityType}'.");
        }

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
            Context = req.Context,
            Scope = req.Scope,
            AttributeCache = new ConcurrentDictionary<string, Task<Dictionary<(string, string), AttributeTuple>>>(
                concurrencyLevel: 1, capacity: 4)
        };

        var res = await LookupEntityInternal(internalReq, cancellationToken);

        // Collect, deduplicate, sort
        var seen = new HashSet<string>(res.Count);
        foreach (ref readonly var r in CollectionsMarshal.AsSpan(res)) seen.Add(r.EntityId);
        ListPool<LookupEntityResult>.Return(res);

        var sorted = new List<string>(seen);
        sorted.Sort(StringComparer.Ordinal);

        activity?.AddEvent(new ActivityEvent("LookupEntityResult",
            tags: new ActivityTagsCollection(CreateLookupEntityResultAttributes(sorted.Count))));

        // Apply pagination
        if (req.PageSize is not { } pageSize)
            return new LookupEntityPage(sorted, null);

        // Decode cursor (base64 of last entity ID)
        string? afterId = null;
        if (!string.IsNullOrEmpty(req.ContinuationToken))
            afterId = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(req.ContinuationToken));

        // Find starting index
        var startIndex = 0;
        if (afterId is not null)
        {
            var idx = sorted.BinarySearch(afterId, StringComparer.Ordinal);
            startIndex = idx >= 0 ? idx + 1 : ~idx;
        }

        var page = sorted.GetRange(startIndex, Math.Min(pageSize, sorted.Count - startIndex));
        string? nextToken = null;
        if (startIndex + pageSize < sorted.Count)
            nextToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(page[^1]));

        return new LookupEntityPage(page, nextToken);
    }


    private static IEnumerable<KeyValuePair<string, object?>> CreateLookupEntityResultAttributes(int count)
    {
        yield return new KeyValuePair<string, object?>("LookupEntityResultCount", count);
    }

    private static IEnumerable<KeyValuePair<string, object?>> CreateLookupEntitySpanAttributes(LookupEntityRequest req)
    {
        yield return new KeyValuePair<string, object?>("LookupEntityRequest", req);
    }

    private static Task<List<LookupEntityResult>> EmptyPooledListTask() =>
        Task.FromResult(ListPool<LookupEntityResult>.Rent());

    private Task<List<LookupEntityResult>> LookupEntityInternal(LookupEntityRequestInternal req, CancellationToken ct)
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

    private Task<List<LookupEntityResult>> LookupPermission(LookupEntityRequestInternal req, Permission permission, CancellationToken ct)
    {
        var permNode = permission.Tree;

        return permNode.Type == PermissionNodeType.Expression
            ? LookupExpression(req, permNode.ExpressionNode!, ct)
            : LookupLeaf(req, permNode.LeafNode!, ct);
    }

    private Task<List<LookupEntityResult>> LookupExpression(LookupEntityRequestInternal req, PermissionNodeOperation node, CancellationToken ct)
    {
        return node.Operation switch
        {
            PermissionOperation.Intersect => LookupExpressionChildren(req, node.Children, isUnion: false, ct),
            PermissionOperation.Union => LookupExpressionChildren(req, node.Children, isUnion: true, ct),
            PermissionOperation.Negate => LookupNegate(req, node.Children[0], ct),
            _ => throw new InvalidOperationException()
        };
    }

    private async Task<List<LookupEntityResult>> LookupExpressionChildren(LookupEntityRequestInternal req,
        List<PermissionNode> children, bool isUnion, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        if (!isUnion && HasMixedAttributeAndRelationChildren(children))
            return await LookupIntersectionConstrained(req, children, ct);

        if (!isUnion && HasNegateAndPositiveChildren(children))
            return await LookupIntersectionWithNegate(req, children, ct);

        var pool = ArrayPool<Task<List<LookupEntityResult>>>.Shared;
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

    private static bool HasMixedAttributeAndRelationChildren(List<PermissionNode> children)
    {
        bool hasAttr = false, hasOther = false;
        foreach (var child in children)
        {
            if (child.Type == PermissionNodeType.Leaf && child.LeafNode!.Type == PermissionNodeLeafType.Expression)
                hasAttr = true;
            else
                hasOther = true;
            if (hasAttr && hasOther) return true;
        }
        return false;
    }

    private async Task<List<LookupEntityResult>> LookupIntersectionConstrained(
        LookupEntityRequestInternal req, List<PermissionNode> children, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var pool = ArrayPool<Task<List<LookupEntityResult>>>.Shared;

        var attrLeaves = new List<PermissionNodeLeafExp>(2);
        var nonAttrCount = 0;
        var nonAttrBuffer = pool.Rent(children.Count);
        List<LookupEntityResult>[] nonAttrResults;
        try
        {
            foreach (var child in children)
            {
                if (child.Type == PermissionNodeType.Leaf &&
                    child.LeafNode!.Type == PermissionNodeLeafType.Expression)
                    attrLeaves.Add(child.LeafNode.ExpressionNode!);
                else
                    nonAttrBuffer[nonAttrCount++] = child.Type == PermissionNodeType.Expression
                        ? LookupExpression(req, child.ExpressionNode!, ct)
                        : LookupLeaf(req, child.LeafNode!, ct);
            }
            nonAttrResults = await Task.WhenAll(new ArraySegment<Task<List<LookupEntityResult>>>(nonAttrBuffer, 0, nonAttrCount));
        }
        finally
        {
            pool.Return(nonAttrBuffer, clearArray: true);
        }

        foreach (var r in nonAttrResults)
        {
            if (r.Count == 0)
            {
                foreach (var rr in nonAttrResults) ListPool<LookupEntityResult>.Return(rr);
                return ListPool<LookupEntityResult>.Rent();
            }
        }

        var first = nonAttrResults[0];
        var entityIds = new HashSet<string>(first.Count);
        foreach (var e in first) entityIds.Add(e.EntityId);

        if (nonAttrResults.Length > 1)
        {
            var tempIds = new HashSet<string>();
            for (var i = 1; i < nonAttrResults.Length; i++)
            {
                tempIds.Clear();
                foreach (var e in nonAttrResults[i]) tempIds.Add(e.EntityId);
                entityIds.IntersectWith(tempIds);
                if (entityIds.Count == 0)
                {
                    foreach (var r in nonAttrResults) ListPool<LookupEntityResult>.Return(r);
                    return ListPool<LookupEntityResult>.Rent();
                }
            }
        }

        var totalCount = nonAttrCount + attrLeaves.Count;
        var attrBuffer = pool.Rent(attrLeaves.Count);
        var allBuffer = pool.Rent(totalCount);
        try
        {
            for (var i = 0; i < attrLeaves.Count; i++)
                attrBuffer[i] = CheckLeafExpWithEntityIds(req, attrLeaves[i], entityIds, ct);

            for (var i = 0; i < nonAttrResults.Length; i++)
                allBuffer[i] = Task.FromResult(nonAttrResults[i]);
            for (var i = 0; i < attrLeaves.Count; i++)
                allBuffer[nonAttrResults.Length + i] = attrBuffer[i];

            return await IntersectEntities(allBuffer, totalCount);
        }
        finally
        {
            pool.Return(attrBuffer, clearArray: true);
            pool.Return(allBuffer, clearArray: true);
        }
    }

    private async Task<List<LookupEntityResult>> CheckLeafExpWithEntityIds(
        LookupEntityRequestInternal req, PermissionNodeLeafExp node,
        IReadOnlyCollection<string> entityIds, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        var fn = schema.Functions[node.FunctionName];
        if (fn is null) throw new InvalidOperationException();
        if (!node.IsContextValid(req.Context)) return ListPool<LookupEntityResult>.Rent();

        var attributeArguments = node.GetArgsAttributesNames();
        var attributes = await reader.GetAttributesWithEntityIds(
            new EntityAttributesFilter
            {
                Attributes = attributeArguments,
                EntityType = req.EntityType,
                SnapToken = req.SnapToken ?? SnapToken.MinValue
            },
            entityIds,
            ct);

        using var paramToArgMap = fn.CreateParamToArgMap(node.Args);
        return EvaluateExpressionMatches(attributes, req, fn, paramToArgMap);
    }

    private Task<List<LookupEntityResult>> LookupLeaf(LookupEntityRequestInternal req, PermissionNodeLeaf node, CancellationToken ct)
    {
        return node.Type switch
        {
            PermissionNodeLeafType.Permission => CheckLeafPermission(req, node.PermissionNode!, ct),
            PermissionNodeLeafType.Expression => CheckLeafExp(req, node.ExpressionNode!, ct),
            _ => throw new InvalidOperationException()
        };
    }

    private Task<List<LookupEntityResult>> CheckLeafPermission(LookupEntityRequestInternal req,
        PermissionNodeLeafPermission node, CancellationToken ct)
    {
        if (node.IsIndirect)
            return CheckTupleToUserSet(req, node.UserSet!, node.ComputedUserSet!, ct);
        return LookupComputedUserSet(req, node.Permission, ct);
    }

    private async Task<List<LookupEntityResult>> CheckLeafExp(LookupEntityRequestInternal req,
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
            return ListPool<LookupEntityResult>.Rent();
        }

        var attributeArguments = node.GetArgsAttributesNames();

        var cacheKey = req.EntityType + "\x00" + string.Join(",", attributeArguments) + "\x00" + req.Scope?.Relation + "\x00" + req.Scope?.SubjectType + "\x00" + req.Scope?.SubjectId;
        var attributesTask = req.AttributeCache.GetOrAdd(cacheKey,
            static (_, state) => state.reader.GetAttributes(
                new EntityAttributesFilter
                {
                    Attributes = state.attributeArguments,
                    EntityType = state.req.EntityType,
                    SnapToken = state.req.SnapToken ?? SnapToken.MinValue
                }, state.req.Scope, state.ct),
            (reader, attributeArguments, req, ct));
        var attributes = await attributesTask;

        using var paramToArgMap = fn.CreateParamToArgMap(node.Args);

        return EvaluateExpressionMatches(attributes, req, fn, paramToArgMap);
    }

    private List<LookupEntityResult> EvaluateExpressionMatches(
        IReadOnlyDictionary<(string AttributeName, string EntityId), AttributeTuple> attributes,
        LookupEntityRequestInternal req,
        Function fn,
        PooledDictionary<FunctionParameter, PermissionNodeExpArgument> paramToArgMap)
    {
        var result = ListPool<LookupEntityResult>.Rent();

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
                result.Add(new LookupEntityResult(attr.EntityType, attr.EntityId));
            }
        }

        return result;
    }

    private async Task<List<LookupEntityResult>> CheckTupleToUserSet(LookupEntityRequestInternal req,
        string tupleSetRelation, string computedUserSetRelation, CancellationToken ct)
    {
        var relation = schema.GetRelation(req.EntityType, tupleSetRelation);
        var pool = ArrayPool<Task<List<LookupEntityResult>>>.Shared;
        var buffer = pool.Rent(relation.Entities.Count);
        var count = 0;
        try
        {
            foreach (var entity in relation.Entities)
            {
                // Fast path: when the computed relation is a terminal direct relation that
                // directly accepts the final subject type, collapse the two-hop traversal
                // (dependent lookup + main leaf lookup) into a single joined DB query.
                // Depth guard mirrors the depth check that LookupEntityInternal would apply
                // to the dependent leg — if depth is already 0, the dependent returns empty.
                if (entity.Relation is null
                    && req.Depth > 0
                    && schema.GetRelationType(entity.Type, computedUserSetRelation) == RelationType.DirectRelation)
                {
                    var computedRel = schema.GetRelation(entity.Type, computedUserSetRelation);
                    if (!computedRel.HasSubRelationPaths && computedRel.EntityTypes.Contains(req.FinalSubjectType))
                    {
                        buffer[count++] = JoinedLookup(
                            new EntityRelationFilter { EntityType = req.EntityType, Relation = tupleSetRelation, SnapToken = req.SnapToken ?? SnapToken.MinValue },
                            entity.Type, computedUserSetRelation,
                            req.FinalSubjectType, req.FinalSubjectId, req.Scope, ct);
                        continue;
                    }
                }

                var dependent = LookupEntityInternal(req with
                {
                    EntityType = entity.Type, Permission = computedUserSetRelation,
                    Scope = null,  // clear scope — intermediate types are not scoped
                }, ct);

                buffer[count++] = JoinEntities(
                    static (relatedTuples, s) =>
                    {
                        using var activityMain = DefaultActivitySource.InternalSourceInstance.StartActivity("join main FN");
                        if (relatedTuples.Count > 0)
                        {
                            return s.engine.LookupRelationLeaf(s.req with
                            {
                                Permission = s.tupleSetRelation,
                                EntityType = s.req.EntityType,
                                SubjectType = s.entity.Type,
                                SubjectsIds = ToEntityIdList(relatedTuples),
                                SubjectRelation = s.entity.Relation,
                                Depth = s.req.Depth
                            }, s.ct);
                        }
                        return EmptyPooledListTask();
                    },
                    (engine: this, req, entity, tupleSetRelation, ct),
                    dependent);
            }

            return await UnionEntities(buffer, count);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private Task<List<LookupEntityResult>> LookupComputedUserSet(LookupEntityRequestInternal req,
        string computedUserSetRelation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        return LookupEntityInternal(req with { Permission = computedUserSetRelation }, ct);
    }

    private async Task<List<LookupEntityResult>> LookupAttribute(LookupEntityRequestInternal req,
        Schemas.Attribute attribute, CancellationToken ct)
    {
        HashSet<string>? scopedEntityIds = null;
        if (req.Scope is { } scope)
        {
            using var scopeRelations = await reader.GetRelationsWithSubjectsIds(
                new EntityRelationFilter
                {
                    EntityType = req.EntityType,
                    Relation = scope.Relation,
                    SnapToken = req.SnapToken ?? SnapToken.MinValue
                },
                [scope.SubjectId],
                scope.SubjectType,
                null,
                ct);
            if (scopeRelations.Count == 0)
                return ListPool<LookupEntityResult>.Rent();
            scopedEntityIds = new HashSet<string>(scopeRelations.Count);
            foreach (var r in scopeRelations) scopedEntityIds.Add(r.EntityId);
        }

        var attrs = await reader.GetAttributes(
            new EntityAttributeFilter
            {
                Attribute = attribute.Name, EntityType = req.EntityType, SnapToken = req.SnapToken ?? SnapToken.MinValue
            }, ct);
        var result = ListPool<LookupEntityResult>.Rent();
        foreach (var a in attrs)
        {
            if (scopedEntityIds is not null && !scopedEntityIds.Contains(a.EntityId)) continue;
            if (a.Value.TryGetValue(out bool b) && b)
                result.Add(new LookupEntityResult(a.EntityType, a.EntityId));
        }
        return result;
    }

    private Task<List<LookupEntityResult>> LookupRelation(LookupEntityRequestInternal req, Relation relation, CancellationToken ct)
    {
        if (!relation.EntityTypes.Contains(req.FinalSubjectType) && !relation.HasSubRelationPaths)
            return EmptyPooledListTask();

        return LookupRelationCore(req, relation, ct);
    }

    private async Task<List<LookupEntityResult>> LookupRelationCore(LookupEntityRequestInternal req, Relation relation, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var pool = ArrayPool<Task<List<LookupEntityResult>>>.Shared;
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
                    // Fast path: terminal sub-relation that directly accepts the final subject
                    // type — collapse two GetRelationsWithSubjectsIds calls into one joined query.
                    if (!subRelation.HasSubRelationPaths && subRelation.EntityTypes.Contains(req.FinalSubjectType))
                    {
                        buffer[count++] = JoinedLookup(
                            new EntityRelationFilter { EntityType = req.EntityType, Relation = relation.Name, SnapToken = req.SnapToken ?? SnapToken.MinValue },
                            relationEntity.Type, relationEntity.Relation!,
                            req.FinalSubjectType, req.FinalSubjectId, req.Scope, ct);
                        continue;
                    }

                    var dependent = LookupRelation(req with
                    {
                        EntityType = relationEntity.Type, Permission = relationEntity.Relation!,
                        Scope = null,  // clear scope — sub-relation traversal is not scoped
                    }, subRelation, ct);

                    buffer[count++] = JoinEntities(
                        static (relatedTuples, s) =>
                        {
                            using var activityMain =
                                DefaultActivitySource.InternalSourceInstance.StartActivity("join main FN");
                            if (relatedTuples.Count > 0)
                            {
                                return s.engine.LookupRelationLeaf(s.req with
                                {
                                    Permission = s.relation.Name,
                                    EntityType = s.req.EntityType,
                                    SubjectType = s.relationEntity.Type,
                                    SubjectsIds = ToEntityIdList(relatedTuples),
                                    SubjectRelation = s.relationEntity.Relation,
                                    Depth = s.req.Depth
                                }, s.ct);
                            }
                            return EmptyPooledListTask();
                        },
                        (engine: this, req, relation, relationEntity, ct),
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

    private async Task<List<LookupEntityResult>> LookupRelationLeaf(LookupEntityRequestInternal req, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        using var relations = await reader.GetRelationsWithSubjectsIds(
            new EntityRelationFilter
            {
                Relation = req.Permission, EntityType = req.EntityType, SnapToken = req.SnapToken ?? SnapToken.MinValue
            },
            req.SubjectsIds,
            req.SubjectType,
            req.Scope,
            ct);
        var result = ListPool<LookupEntityResult>.Rent();
        foreach (var x in relations) result.Add(new LookupEntityResult(x.EntityType, x.EntityId, x.SubjectType, x.SubjectId));
        return result;
    }

    private async Task<List<LookupEntityResult>> JoinedLookup(
        EntityRelationFilter mainFilter, string subEntityType, string subRelation,
        string subjectType, string subjectId, EntityScope? scope, CancellationToken ct)
    {
        using var relations = await reader.GetRelationsJoined(mainFilter, subEntityType, subRelation, subjectType, subjectId, scope, ct);
        var result = ListPool<LookupEntityResult>.Rent();
        foreach (var x in relations) result.Add(new LookupEntityResult(x.EntityType, x.EntityId, x.SubjectType, x.SubjectId));
        return result;
    }

    private static async Task<List<LookupEntityResult>> JoinEntities<TState>(
        Func<List<LookupEntityResult>, TState, Task<List<LookupEntityResult>>> main,
        TState state,
        Task<List<LookupEntityResult>> dependent
    )
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var dependentResult = await dependent;
        if (dependentResult.Count == 0)
            return dependentResult;

        var mainResult = await main(dependentResult, state);

        var dependentSet = new HashSet<(string Type, string Id)>(dependentResult.Count);
        foreach (var d in dependentResult)
            dependentSet.Add((d.EntityType, d.EntityId));
        ListPool<LookupEntityResult>.Return(dependentResult);

        var result = ListPool<LookupEntityResult>.Rent();
        foreach (var m in mainResult)
            if (m.SubjectType is not null && dependentSet.Contains((m.SubjectType, m.SubjectId!)))
                result.Add(m);
        ListPool<LookupEntityResult>.Return(mainResult);

        return result;
    }

    private static bool HasNegateAndPositiveChildren(List<PermissionNode> children)
    {
        bool hasNegate = false, hasPositive = false;
        foreach (var child in children)
        {
            if (child.Type == PermissionNodeType.Expression &&
                child.ExpressionNode!.Operation == PermissionOperation.Negate)
                hasNegate = true;
            else
                hasPositive = true;
            if (hasNegate && hasPositive) return true;
        }
        return false;
    }

    private async Task<List<LookupEntityResult>> LookupNegate(
        LookupEntityRequestInternal req, PermissionNode child, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var matching = await (child.Type == PermissionNodeType.Expression
            ? LookupExpression(req, child.ExpressionNode!, ct)
            : LookupLeaf(req, child.LeafNode!, ct));

        List<string> excludeIds;
        try
        {
            excludeIds = new List<string>(matching.Count);
            foreach (ref readonly var m in CollectionsMarshal.AsSpan(matching))
                excludeIds.Add(m.EntityId);
        }
        finally
        {
            ListPool<LookupEntityResult>.Return(matching);
        }

        var complementIds = await reader.GetEntityIdsExcluding(req.EntityType, excludeIds, req.SnapToken ?? SnapToken.MinValue, ct);

        var result = ListPool<LookupEntityResult>.Rent();
        foreach (var id in complementIds)
            result.Add(new LookupEntityResult(req.EntityType, id));
        return result;
    }

    // Fast path B: when Intersect has both positive and Negate children.
    // Evaluates positive children first, then subtracts the Negate children's matches
    // in-memory — avoids the full-table-scan GetEntityIdsExcluding DB call.
    private async Task<List<LookupEntityResult>> LookupIntersectionWithNegate(
        LookupEntityRequestInternal req, List<PermissionNode> children, CancellationToken ct)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var pool = ArrayPool<Task<List<LookupEntityResult>>>.Shared;
        var positiveBuffer = pool.Rent(children.Count);
        var negateBuffer = pool.Rent(children.Count);
        int positiveCount = 0, negateCount = 0;
        List<LookupEntityResult>[]? positiveResults = null;
        List<LookupEntityResult>[]? negateResults = null;

        try
        {
            foreach (var child in children)
            {
                if (child.Type == PermissionNodeType.Expression &&
                    child.ExpressionNode!.Operation == PermissionOperation.Negate)
                {
                    var inner = child.ExpressionNode.Children[0];
                    negateBuffer[negateCount++] = inner.Type == PermissionNodeType.Expression
                        ? LookupExpression(req, inner.ExpressionNode!, ct)
                        : LookupLeaf(req, inner.LeafNode!, ct);
                }
                else
                {
                    positiveBuffer[positiveCount++] = child.Type == PermissionNodeType.Expression
                        ? LookupExpression(req, child.ExpressionNode!, ct)
                        : LookupLeaf(req, child.LeafNode!, ct);
                }
            }

            positiveResults = await Task.WhenAll(new ArraySegment<Task<List<LookupEntityResult>>>(positiveBuffer, 0, positiveCount));
            negateResults = await Task.WhenAll(new ArraySegment<Task<List<LookupEntityResult>>>(negateBuffer, 0, negateCount));

            // Build positive intersection set
            var positiveSet = new HashSet<string>(positiveResults[0].Count);
            foreach (ref readonly var item in CollectionsMarshal.AsSpan(positiveResults[0]))
                positiveSet.Add(item.EntityId);

            for (var i = 1; i < positiveResults.Length; i++)
            {
                var ids = new HashSet<string>(positiveResults[i].Count);
                foreach (ref readonly var item in CollectionsMarshal.AsSpan(positiveResults[i]))
                    ids.Add(item.EntityId);
                positiveSet.IntersectWith(ids);
                if (positiveSet.Count == 0)
                    return ListPool<LookupEntityResult>.Rent();
            }

            // Build exclusion set from all Negate children evaluations
            var excludeSet = new HashSet<string>();
            foreach (var negResult in negateResults)
                foreach (ref readonly var item in CollectionsMarshal.AsSpan(negResult))
                    excludeSet.Add(item.EntityId);

            // Return positiveSet - excludeSet
            var result = ListPool<LookupEntityResult>.Rent();
            foreach (var entityId in positiveSet)
                if (!excludeSet.Contains(entityId))
                    result.Add(new LookupEntityResult(req.EntityType, entityId));
            return result;
        }
        finally
        {
            pool.Return(positiveBuffer, clearArray: true);
            pool.Return(negateBuffer, clearArray: true);
            if (positiveResults is not null)
                foreach (var r in positiveResults) ListPool<LookupEntityResult>.Return(r);
            if (negateResults is not null)
                foreach (var r in negateResults) ListPool<LookupEntityResult>.Return(r);
        }
    }

    private static async Task<List<LookupEntityResult>> UnionEntities(
        Task<List<LookupEntityResult>>[] buffer, int count)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var results = await Task.WhenAll(new ArraySegment<Task<List<LookupEntityResult>>>(buffer, 0, count));

        var merged = ListPool<LookupEntityResult>.Rent();
        foreach (var r in results)
        {
            merged.AddRange(CollectionsMarshal.AsSpan(r));
            ListPool<LookupEntityResult>.Return(r);
        }
        return merged;
    }

    private static async Task<List<LookupEntityResult>> IntersectEntities(
        Task<List<LookupEntityResult>>[] buffer, int count)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var results = await Task.WhenAll(new ArraySegment<Task<List<LookupEntityResult>>>(buffer, 0, count));

        if (results.Length == 0)
            return ListPool<LookupEntityResult>.Rent();

        var hashSet = new HashSet<LookupEntityResult>(results[0], LookupEntityResultComparer.Instance);
        ListPool<LookupEntityResult>.Return(results[0]);
        for (var i = 1; i < results.Length; i++)
        {
            hashSet.IntersectWith(results[i]);
            ListPool<LookupEntityResult>.Return(results[i]);
            if (hashSet.Count == 0)
                return ListPool<LookupEntityResult>.Rent();
        }

        var result = ListPool<LookupEntityResult>.Rent();
        foreach (var item in hashSet) result.Add(item);
        return result;
    }

    private static string[] ToEntityIdList(List<LookupEntityResult> tuples)
    {
        var arr = new string[tuples.Count];
        var span = CollectionsMarshal.AsSpan(tuples);
        for (var i = 0; i < span.Length; i++) arr[i] = span[i].EntityId;
        return arr;
    }
}

internal readonly struct LookupEntityResult : IEquatable<LookupEntityResult>
{
    public LookupEntityResult(string entityType, string entityId, string? subjectType = null, string? subjectId = null)
    {
        EntityType = entityType;
        EntityId = entityId;
        SubjectType = subjectType;
        SubjectId = subjectId;
    }

    public string EntityType { get; }
    public string EntityId { get; }
    public string? SubjectType { get; }
    public string? SubjectId { get; }

    public bool Equals(LookupEntityResult other) =>
        EntityType == other.EntityType && EntityId == other.EntityId;

    public override bool Equals(object? obj) =>
        obj is LookupEntityResult other && Equals(other);

    public override int GetHashCode()
    {
        unchecked { return (EntityType.GetHashCode() * 397) ^ EntityId.GetHashCode(); }
    }
}

internal sealed class LookupEntityResultComparer : IEqualityComparer<LookupEntityResult>
{
    private LookupEntityResultComparer() { }
    internal static IEqualityComparer<LookupEntityResult> Instance { get; } = new LookupEntityResultComparer();

    public bool Equals(LookupEntityResult x, LookupEntityResult y) =>
        x.EntityType == y.EntityType && x.EntityId == y.EntityId;

    public int GetHashCode(LookupEntityResult obj)
    {
        unchecked { return (obj.EntityType.GetHashCode() * 397) ^ obj.EntityId.GetHashCode(); }
    }
}
