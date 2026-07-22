using System.Data.Common;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Pools;
using Valtuutus.Data.Db;
using Valtuutus.Data.SqlServer;

namespace Valtuutus.Data.SqlServer.Tests;

/// <summary>
/// Shared physical-round-trip counter for the DbBatch round-trip-reduction proof, mirroring
/// <c>Valtuutus.Data.Postgres.Tests.RoundTripCountingReaderProvider</c> (same technique, same
/// reasoning — see that file's doc comment for the full rationale). One instance per measured
/// run, shared between <see cref="CountingReaderProvider"/> (individual per-op round trips) and
/// <see cref="CountingBatchOps"/> (one round trip per whole batch, regardless of how many
/// commands it carries) so both contribute to the same total.
/// </summary>
internal sealed class RoundTripCounter
{
    private int _roundTrips;

    public int Count => Volatile.Read(ref _roundTrips);

    public void Increment() => Interlocked.Increment(ref _roundTrips);
}

/// <summary>
/// Wraps a real <see cref="SqlServerDataReaderProvider"/> and counts physical round trips. Every
/// <see cref="IDataReaderProvider"/>/<see cref="IRelationalCheckOps"/> member is its own round
/// trip when invoked individually, so each is counted. Used for BOTH the batching and
/// individual-dispatch runs — the batching run's savings show up as calls that never reach this
/// decorator at all, routed instead through <see cref="CountingBatchOps"/>.
/// </summary>
internal sealed class CountingReaderProvider(SqlServerDataReaderProvider inner, RoundTripCounter counter)
    : IDataReaderProvider, IRelationalCheckOps
{
    public int RoundTripCount => counter.Count;

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetLatestSnapToken(cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetRelations(tupleFilter, cancellationToken);
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.HasDirectRelation(tupleFilter, subjectId, cancellationToken);
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation, string subjectId,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.HasAnyDirectRelation(entityType, entityIds, relation, subjectId, snapToken, cancellationToken);
    }

    public async Task<HashSet<string>> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.HasAnyOfDirectRelations(entityType, entityId, relationNames, subjectId, snapToken, cancellationToken);
    }

    public async Task<HashSet<string>> HasAnyOfAttributes(string entityType, string entityId, string[] attributeNames,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.HasAnyOfAttributes(entityType, entityId, attributeNames, snapToken, cancellationToken);
    }

    public async Task<bool> HasTupleToUserSetRelation(string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken,
        CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.HasTupleToUserSetRelation(entityType, entityId, tupleSetRelation, subEntityType,
            computedRelation, subjectType, subjectId, snapToken, cancellationToken);
    }

    public async Task<bool> HasUsersetJoinRelation(string entityType, string entityId, string relation,
        string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken,
        CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.HasUsersetJoinRelation(entityType, entityId, relation, subEntityType, computedRelation,
            subjectType, subjectId, snapToken, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetIndirectRelations(tupleFilter, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter,
        string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entityIds, subjectRelation, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
        string[] subjectsIds, string subjectType, EntityScope? scope, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetRelationsWithSubjectsIds(entityFilter, subjectsIds, subjectType, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIdsMultiRelation(string entityType,
        string[] relationNames, string[] subjectsIds, string subjectType, SnapToken snapToken, EntityScope? scope,
        CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetRelationsWithSubjectsIdsMultiRelation(entityType, relationNames, subjectsIds, subjectType,
            snapToken, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIdsMultiRelation(string entityType,
        string[] relationNames, string subjectType, IEnumerable<string> entityIds, string? subjectRelation,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetRelationsWithEntityIdsMultiRelation(entityType, relationNames, subjectType, entityIds,
            subjectRelation, snapToken, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(EntityRelationFilter mainFilter, string subEntityType,
        string subRelation, string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetRelationsJoined(mainFilter, subEntityType, subRelation, subjectType, subjectId, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoinedByEntityIds(EntityRelationFilter mainFilter,
        IEnumerable<string> entityIds, string subEntityType, string subRelation, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetRelationsJoinedByEntityIds(mainFilter, entityIds, subEntityType, subRelation, cancellationToken);
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetAttribute(filter, cancellationToken);
    }

    public async Task<bool> HasTrueBoolAttribute(string entityType, string entityId, string attribute, SnapToken snapToken,
        CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.HasTrueBoolAttribute(entityType, entityId, attribute, snapToken, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetAttributes(filter, cancellationToken);
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(
        EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetAttributes(filter, scope, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetAttributesWithEntityIds(filter, entitiesIds, cancellationToken);
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(
        EntityAttributesFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetAttributesWithEntityIds(filter, entitiesIds, cancellationToken);
    }

    public async Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetAttributesSingleEntity(filter, cancellationToken);
    }

    public async Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetEntityIdsExcluding(entityType, excludeIds, snapToken, cancellationToken);
    }

    public async Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        counter.Increment();
        return await inner.GetSubjectIdsExcluding(subjectType, excludeIds, snapToken, cancellationToken);
    }
}

/// <summary>
/// Wraps a real <see cref="SqlServerBatchOps"/> (or any <see cref="IRelationalBatchOps"/>) and
/// counts one round trip per <see cref="ExecuteBatchAsync"/> call — the one physical round trip a
/// whole batch costs regardless of how many commands it carries. CreateBatch/Add* never touch the
/// network, so they are NOT counted, per <see cref="IRelationalBatchOps"/>'s own doc comments.
/// </summary>
internal sealed class CountingBatchOps(IRelationalBatchOps inner, RoundTripCounter counter) : IRelationalBatchOps
{
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

    public void AddHasAnyDirectRelationToBatch(DbBatch batch, string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken)
        => inner.AddHasAnyDirectRelationToBatch(batch, entityType, entityIds, relation, subjectId, snapToken);

    public void AddGetRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
        => inner.AddGetRelationsToBatch(batch, tupleFilter);

    public void AddGetIndirectRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
        => inner.AddGetIndirectRelationsToBatch(batch, tupleFilter);

    public async Task<DbDataReader> ExecuteBatchAsync(DbBatch batch, CancellationToken cancellationToken)
    {
        // One physical round trip regardless of how many commands `batch` carries.
        counter.Increment();
        return await inner.ExecuteBatchAsync(batch, cancellationToken);
    }
}
