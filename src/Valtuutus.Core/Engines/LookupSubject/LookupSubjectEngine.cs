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
        // Skip the tags iterator allocation entirely when nothing is listening.
        using var activity = DefaultActivitySource.Instance.HasListeners()
            ? DefaultActivitySource.Instance.StartActivity(ActivityKind.Internal,
                tags: CreateLookupSubjectSpanAttributes(req))
            : null;
        var snapToken = await SnapTokenUtils.ResolveLatest(reader, req.SnapToken, cancellationToken);

        if (!schema.CanSubjectTypeReach(req.EntityType, req.Permission, req.SubjectType))
            return [];

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

        // A cascading hop (e.g. parent.is_member) can dead-end and produce an empty entity-id set.
        // There are no entities left to resolve subjects for, so the answer is empty. Short-circuiting
        // here also keeps the empty list out of the data layer, where the SQL builders would otherwise
        // drop the entity_id predicate entirely and return every subject system-wide.
        if (req.EntitiesIds.Count == 0)
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

    // A leaf that is a direct, non-indirect permission reference and is statically unreachable
    // for req.FinalSubjectType (per schema-precomputed reachability) can only ever contribute an
    // empty result. FinalSubjectType — not SubjectType — is the right field to prune against:
    // SubjectType gets reassigned per recursion hop, while FinalSubjectType stays fixed as the
    // true ultimate subject for the whole Lookup call, same convention LookupRelation's existing
    // (weaker, single-hop) guard already uses.
    private bool IsStaticallyDeadForSubject(LookupSubjectRequestInternal req, PermissionNode child)
    {
        if (child.Type != PermissionNodeType.Leaf) return false;
        var leaf = child.LeafNode!;
        if (leaf.Type != PermissionNodeLeafType.Permission) return false;
        var permLeaf = leaf.PermissionNode!;
        if (permLeaf.IsIndirect) return false;
        return !schema.CanSubjectTypeReach(req.EntityType, permLeaf.Permission, req.FinalSubjectType);
    }

    // A live Union/Intersect child that is a plain direct-relation leaf on req.EntityType, with
    // no sub-relation paths and reachable by req.FinalSubjectType, can be resolved via
    // GetRelationsWithEntityIdsMultiRelation alongside its siblings instead of its own
    // per-child LookupRelationLeaf round trip. Mirrors LookupEntityEngine.IsBatchableDirectRelation.
    private bool IsBatchableDirectRelation(LookupSubjectRequestInternal req, PermissionNode child, out string relationName)
    {
        relationName = "";
        if (child.Type != PermissionNodeType.Leaf) return false;
        var leaf = child.LeafNode!;
        if (leaf.Type != PermissionNodeLeafType.Permission) return false;
        var permLeaf = leaf.PermissionNode!;
        if (permLeaf.IsIndirect) return false;
        if (schema.GetRelationType(req.EntityType, permLeaf.Permission) != RelationType.DirectRelation) return false;
        var relation = schema.GetRelation(req.EntityType, permLeaf.Permission);
        if (relation.HasSubRelationPaths) return false;
        if (!relation.EntityTypes.Contains(req.FinalSubjectType)) return false;
        relationName = permLeaf.Permission;
        return true;
    }

    // Resolves a single Union/Intersect child, short-circuiting through a shared sibling-relation
    // batch when possible instead of the normal LookupExpression/LookupLeaf dispatch chain.
    private Task<RelationOrAttributeTuples> ResolveChild(LookupSubjectRequestInternal req,
        Dictionary<string, RelationOrAttributeTuples>? batchResults, PermissionNode child, CancellationToken ct)
    {
        if (batchResults is not null
            && IsBatchableDirectRelation(req, child, out var relationName)
            && batchResults.Remove(relationName, out var cached))
            return Task.FromResult(cached);

        return child.Type == PermissionNodeType.Expression
            ? LookupExpression(req, child.ExpressionNode!, ct)
            : LookupLeaf(req, child.LeafNode!, ct);
    }

    private async Task<RelationOrAttributeTuples> LookupExpressionChildren(LookupSubjectRequestInternal req,
        List<PermissionNode> children, CancellationToken ct, bool isUnion)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var totalCount = children.Count;

        if (!isUnion)
        {
            // Intersect: a single statically-unreachable child makes the whole node empty —
            // short-circuit without spawning a task for it or any sibling.
            for (var i = 0; i < totalCount; i++)
            {
                if (IsStaticallyDeadForSubject(req, children[i]))
                    return _emptyTuples;
            }
        }

        // Union: drop statically-dead children before spawning anything for them — they can
        // only contribute an empty result.
        var live = children;
        if (isUnion)
        {
            List<PermissionNode>? filtered = null;
            for (var i = 0; i < totalCount; i++)
            {
                if (!IsStaticallyDeadForSubject(req, children[i]))
                {
                    filtered?.Add(children[i]);
                    continue;
                }

                if (filtered is null)
                {
                    filtered = new List<PermissionNode>(totalCount);
                    for (var j = 0; j < i; j++) filtered.Add(children[j]);
                }
            }
            if (filtered is not null) live = filtered;
            if (live.Count == 0) return _emptyTuples;
        }

        // Batch sibling direct-relation leaves on req.EntityType into a single provider call
        // instead of N separate LookupRelationLeaf round trips.
        Dictionary<string, RelationOrAttributeTuples>? batchResults = null;
        List<string>? toFetch = null;
        for (var i = 0; i < live.Count; i++)
        {
            if (!IsBatchableDirectRelation(req, live[i], out var relationName)) continue;
            if (toFetch is not null && toFetch.Contains(relationName)) continue;
            (toFetch ??= new List<string>()).Add(relationName);
        }
        if (toFetch is { Count: >= 2 })
        {
            using var rows = await reader.GetRelationsWithEntityIdsMultiRelation(
                req.EntityType, toFetch.ToArray(), req.FinalSubjectType, req.EntitiesIds, req.SubjectRelation,
                req.SnapToken!.Value, ct);

            var buckets = new Dictionary<string, List<RelationTuple>>(toFetch.Count);
            foreach (var name in toFetch) buckets[name] = new List<RelationTuple>();
            foreach (var row in rows) buckets[row.Relation].Add(row);

            batchResults = new Dictionary<string, RelationOrAttributeTuples>(toFetch.Count);
            foreach (var (name, list) in buckets)
                batchResults[name] = new RelationOrAttributeTuples(list);
        }

        var pool = ArrayPool<Task<RelationOrAttributeTuples>>.Shared;
        var buffer = pool.Rent(live.Count);
        try
        {
            for (var i = 0; i < live.Count; i++)
                buffer[i] = ResolveChild(req, batchResults, live[i], ct);

            return isUnion
                ? await UnionEntities(buffer, live.Count)
                : await IntersectEntities(buffer, live.Count);
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

        var attributeArguments = node.AttributeArgNames;

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
                // Fast path: when the computed relation is a terminal direct relation that
                // directly accepts the final subject type, collapse the two-hop traversal
                // (dependent lookup + recursive resolve) into a single joined DB query.
                // Depth guard mirrors the depth check LookupInternal would apply to the
                // dependent leg — if depth is already 0, the dependent returns empty.
                if (entity.Relation is null
                    && req.Depth > 0
                    && schema.GetRelationType(entity.Type, computedUserSetRelation) == RelationType.DirectRelation)
                {
                    var computedRel = schema.GetRelation(entity.Type, computedUserSetRelation);
                    if (!computedRel.HasSubRelationPaths && computedRel.EntityTypes.Contains(req.FinalSubjectType))
                    {
                        buffer[count++] = JoinedLookup(
                            new EntityRelationFilter
                            {
                                Relation = relation.Name, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
                            },
                            req.EntitiesIds, entity.Type, computedUserSetRelation, ct);
                        continue;
                    }
                }

                var dependentTask = reader.GetRelationsWithEntityIds(
                    new EntityRelationFilter
                    {
                        Relation = relation.Name, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
                    },
                    entity.Type,
                    req.EntitiesIds,
                    entity.Relation,
                    ct
                );

                buffer[count++] = JoinEntities(
                    static (subjectIds, state) => state.engine.LookupInternal(state.req with
                    {
                        EntityType = state.entity.Type,
                        Permission = state.computedUserSetRelation,
                        EntitiesIds = subjectIds
                    }, state.ct),
                    (engine: this, req, entity, computedUserSetRelation, ct),
                    dependentTask);
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
                    // Fast path: terminal sub-relation that directly accepts the final subject
                    // type — collapse two GetRelationsWithEntityIds calls into one joined query.
                    if (!subRelation.HasSubRelationPaths && subRelation.EntityTypes.Contains(req.FinalSubjectType))
                    {
                        buffer[count++] = JoinedLookup(
                            new EntityRelationFilter
                            {
                                Relation = relation.Name, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
                            },
                            req.EntitiesIds, relationEntity.Type, relationEntity.Relation!, ct);
                        continue;
                    }

                    var dependentTask = reader.GetRelationsWithEntityIds(
                        new EntityRelationFilter
                        {
                            Relation = req.Permission, EntityType = req.EntityType, SnapToken = req.SnapToken.Value
                        },
                        relationEntity.Type,
                        req.EntitiesIds,
                        subRelation.Name,
                        ct
                    );

                    buffer[count++] = JoinEntities(
                        static (subjectIds, state) => state.engine.LookupRelation(
                            state.req with
                            {
                                EntityType = state.relationEntity.Type,
                                Permission = state.relationEntity.Relation!,
                                EntitiesIds = subjectIds
                            }, state.subRelation, state.ct),
                        (engine: this, req, relationEntity, subRelation, ct),
                        dependentTask);
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

    private async Task<RelationOrAttributeTuples> JoinedLookup(
        EntityRelationFilter mainFilter, IEnumerable<string> entityIds, string subEntityType, string subRelation,
        CancellationToken ct)
    {
        var relations = await reader.GetRelationsJoinedByEntityIds(mainFilter, entityIds, subEntityType, subRelation, ct);
        return new RelationOrAttributeTuples(relations.Transfer());
    }

    // Defers the dependent-relation query so sibling branches in the caller's loop can fire
    // their own dependent queries concurrently instead of waiting for this one to complete
    // before even starting. Mirrors LookupEntityEngine's JoinEntities, minus the join-back
    // filter — that engine needs it because it queries "backward" toward one known final
    // subject, whereas this recursion just narrows EntitiesIds to whatever the dependent query
    // found, so there's nothing to filter after the fact.
    private static async Task<RelationOrAttributeTuples> JoinEntities<TState>(
        Func<string[], TState, Task<RelationOrAttributeTuples>> main,
        TState state,
        Task<PooledList<RelationTuple>> dependent)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();

        var dependentResult = await dependent;
        if (dependentResult.Count == 0)
            return _emptyTuples;

        return await main(ToSubjectIdList(dependentResult.Transfer()), state);
    }

    private static string[] ToSubjectIdList(List<RelationTuple> tuples)
    {
        using var pooled = PooledHashSet<string>.Rent();
        var seen = pooled.Set;
        seen.EnsureCapacity(tuples.Count);
        foreach (ref readonly var t in CollectionsMarshal.AsSpan(tuples)) seen.Add(t.SubjectId);
        var arr = new string[seen.Count];
        seen.CopyTo(arr);
        return arr;
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
