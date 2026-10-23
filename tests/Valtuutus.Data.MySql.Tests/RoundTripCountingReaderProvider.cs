using System.Data.Common;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Pools;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql.Tests;

/// <summary>
/// Physical round-trip count for one measured run, shared between
/// <see cref="CountingReaderProvider"/> (one round trip per individual reader call) and
/// <see cref="CountingBatchOps"/> (one round trip per whole batch, regardless of how many
/// commands it carries) so both contribute to the same total.
/// </summary>
internal sealed class RoundTripCounter
{
    private int _roundTrips;

    public int Count => Volatile.Read(ref _roundTrips);

    public void Increment() => Interlocked.Increment(ref _roundTrips);

    /// <summary>Runs before every counted round trip is dispatched, with that round trip's
    /// 1-based number — lets a spec act at a precise point between round trips.</summary>
    public Func<int, Task>? BeforeRoundTrip { get; set; }

    public async Task CountAsync()
    {
        var roundTrip = Interlocked.Increment(ref _roundTrips);
        if (BeforeRoundTrip is { } hook)
            await hook(roundTrip);
    }
}

/// <summary>
/// Wraps a real <see cref="MySqlDataReaderProvider"/> and counts physical round trips. Every
/// <see cref="IDataReaderProvider"/>/<see cref="IRelationalCheckOps"/> member is its own round
/// trip when invoked individually, so each is counted. A batching run's savings show up as calls
/// that never reach this decorator, routed instead through <see cref="CountingBatchOps"/>.
/// </summary>
internal sealed class CountingReaderProvider(MySqlDataReaderProvider inner, RoundTripCounter counter)
    : IDataReaderProvider, IRelationalCheckOps
{
    public int RoundTripCount => counter.Count;

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetLatestSnapToken(cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetRelations(tupleFilter, cancellationToken);
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.HasDirectRelation(tupleFilter, subjectId, cancellationToken);
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation, string subjectId,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.HasAnyDirectRelation(entityType, entityIds, relation, subjectId, snapToken, cancellationToken);
    }

    public async Task<HashSet<string>> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.HasAnyOfDirectRelations(entityType, entityId, relationNames, subjectId, snapToken, cancellationToken);
    }

    public async Task<HashSet<string>> HasAnyOfAttributes(string entityType, string entityId, string[] attributeNames,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.HasAnyOfAttributes(entityType, entityId, attributeNames, snapToken, cancellationToken);
    }

    public async Task<bool> HasTupleToUserSetRelation(string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken,
        CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.HasTupleToUserSetRelation(entityType, entityId, tupleSetRelation, subEntityType,
            computedRelation, subjectType, subjectId, snapToken, cancellationToken);
    }

    public async Task<bool> HasUsersetJoinRelation(string entityType, string entityId, string relation,
        string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken,
        CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.HasUsersetJoinRelation(entityType, entityId, relation, subEntityType, computedRelation,
            subjectType, subjectId, snapToken, cancellationToken);
    }

    public async Task<bool> HasFusedExpression(string entityType, string entityId, IReadOnlyList<FusedCheckLeaf> leaves,
        bool requireAll, string? subjectType, string? subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.HasFusedExpression(entityType, entityId, leaves, requireAll, subjectType, subjectId,
            snapToken, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetIndirectRelations(tupleFilter, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter,
        string subjectType, IEnumerable<string> entityIds, string subjectRelation, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entityIds, subjectRelation, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
        string[] subjectsIds, string subjectType, string subjectRelation, EntityScope? scope, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetRelationsWithSubjectsIds(entityFilter, subjectsIds, subjectType, subjectRelation, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIdsMultiRelation(string entityType,
        string[] relationNames, string[] subjectsIds, string subjectType, string subjectRelation, SnapToken snapToken, EntityScope? scope,
        CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetRelationsWithSubjectsIdsMultiRelation(entityType, relationNames, subjectsIds, subjectType,
            subjectRelation, snapToken, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIdsMultiRelation(string entityType,
        string[] relationNames, string subjectType, IEnumerable<string> entityIds, string subjectRelation,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetRelationsWithEntityIdsMultiRelation(entityType, relationNames, subjectType, entityIds,
            subjectRelation, snapToken, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(EntityRelationFilter mainFilter, string subEntityType,
        string subjectRelation, string subRelation, string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetRelationsJoined(mainFilter, subEntityType, subjectRelation, subRelation, subjectType, subjectId, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoinedByEntityIds(EntityRelationFilter mainFilter,
        IEnumerable<string> entityIds, string subEntityType, string subjectRelation, string subRelation,
        string subjectType, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetRelationsJoinedByEntityIds(mainFilter, entityIds, subEntityType, subjectRelation, subRelation, subjectType, cancellationToken);
    }

    public async Task<bool> HasTrueBoolAttribute(string entityType, string entityId, string attribute, SnapToken snapToken,
        CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.HasTrueBoolAttribute(entityType, entityId, attribute, snapToken, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetAttributes(filter, cancellationToken);
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(
        EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetAttributes(filter, scope, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetAttributesWithEntityIds(filter, entitiesIds, cancellationToken);
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(
        EntityAttributesFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetAttributesWithEntityIds(filter, entitiesIds, cancellationToken);
    }

    public async Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetAttributesSingleEntity(filter, cancellationToken);
    }

    public async Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetEntityIdsExcluding(entityType, excludeIds, snapToken, cancellationToken);
    }

    public async Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        await counter.CountAsync();
        return await inner.GetSubjectIdsExcluding(subjectType, excludeIds, snapToken, cancellationToken);
    }
}

/// <summary>
/// Wraps a real <see cref="MySqlBatchOps"/> and counts one round trip per
/// <see cref="ExecuteBatchAsync"/> call, the one physical round trip a whole batch costs. Creating
/// a batch and adding commands to it never touch the network, so they are not counted. Lookup
/// batch ops only add commands to batches created and executed here, so their steps are counted
/// through this wrapper too.
/// </summary>
internal sealed class CountingBatchOps(IRelationalBatchOps inner, RoundTripCounter counter)
    : IRelationalBatchOps
{
    // That view only builds commands; every batch built through it is executed, and counted,
    // through this wrapper's ExecuteBatchAsync.
    public IRelationalBatchOps? UsingBatchResolvedSnapToken => inner.UsingBatchResolvedSnapToken;

    public void AddResolveLatestSnapTokenToBatch(DbBatch batch) => inner.AddResolveLatestSnapTokenToBatch(batch);

    public DbBatch CreateBatch() => inner.CreateBatch();

    public void AddHasAnyOfDirectRelationsToBatch(DbBatch batch, string entityType, string entityId,
        string[] relationNames, string subjectId, SnapToken snapToken)
        => inner.AddHasAnyOfDirectRelationsToBatch(batch, entityType, entityId, relationNames, subjectId, snapToken);

    public void AddHasAnyOfAttributesToBatch(DbBatch batch, string entityType, string entityId,
        string[] attributeNames, SnapToken snapToken)
        => inner.AddHasAnyOfAttributesToBatch(batch, entityType, entityId, attributeNames, snapToken);

    public void AddHasDirectRelationToBatch(DbBatch batch, RelationTupleFilter tupleFilter, string subjectId)
        => inner.AddHasDirectRelationToBatch(batch, tupleFilter, subjectId);

    public void AddHasTrueBoolAttributeToBatch(DbBatch batch, string entityType, string entityId, string attribute,
        SnapToken snapToken)
        => inner.AddHasTrueBoolAttributeToBatch(batch, entityType, entityId, attribute, snapToken);

    public void AddHasTupleToUserSetRelationToBatch(DbBatch batch, string entityType, string entityId,
        string tupleSetRelation, string subEntityType, string computedRelation, string subjectType, string subjectId,
        SnapToken snapToken)
        => inner.AddHasTupleToUserSetRelationToBatch(batch, entityType, entityId, tupleSetRelation, subEntityType,
            computedRelation, subjectType, subjectId, snapToken);

    public void AddHasUsersetJoinRelationToBatch(DbBatch batch, string entityType, string entityId, string relation,
        string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken)
        => inner.AddHasUsersetJoinRelationToBatch(batch, entityType, entityId, relation, subEntityType,
            computedRelation, subjectType, subjectId, snapToken);

    public void AddHasFusedExpressionToBatch(DbBatch batch, string entityType, string entityId,
        IReadOnlyList<FusedCheckLeaf> leaves, bool requireAll, string? subjectType, string? subjectId, SnapToken snapToken)
        => inner.AddHasFusedExpressionToBatch(batch, entityType, entityId, leaves, requireAll, subjectType, subjectId, snapToken);

    public void AddHasAnyDirectRelationToBatch(DbBatch batch, string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken)
        => inner.AddHasAnyDirectRelationToBatch(batch, entityType, entityIds, relation, subjectId, snapToken);

    public void AddGetRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
        => inner.AddGetRelationsToBatch(batch, tupleFilter);

    public void AddGetIndirectRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
        => inner.AddGetIndirectRelationsToBatch(batch, tupleFilter);

    public void AddGetAttributesSingleEntityToBatch(DbBatch batch, string entityType, string entityId,
        string[] attributeNames, SnapToken snapToken)
        => inner.AddGetAttributesSingleEntityToBatch(batch, entityType, entityId, attributeNames, snapToken);

    public async Task<DbDataReader> ExecuteBatchAsync(DbBatch batch, CancellationToken cancellationToken)
    {
        // One physical round trip regardless of how many commands `batch` carries.
        await counter.CountAsync();
        return await inner.ExecuteBatchAsync(batch, cancellationToken);
    }
}
