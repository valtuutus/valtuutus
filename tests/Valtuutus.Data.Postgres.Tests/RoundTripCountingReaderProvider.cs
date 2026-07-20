using System.Data.Common;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Pools;
using Valtuutus.Data.Db;
using Valtuutus.Data.Postgres;

namespace Valtuutus.Data.Postgres.Tests;

/// <summary>
/// Wraps a real <see cref="PostgresDataReaderProvider"/> and counts physical round trips for the
/// round-trip-reduction proof (DbBatch-Task 8, Step 3). Every <see cref="IDataReaderProvider"/>/
/// <see cref="IRelationalCheckOps"/> member is its own round trip when invoked individually, so
/// each is counted. <see cref="IRelationalBatchOps"/>'s Add* members and CreateBatch never touch
/// the network (they only build up a DbBatch in memory) so they are NOT counted here — only
/// ExecuteBatchAsync (in <see cref="BatchingCountingReader"/>) is, since that's the one physical
/// round trip a whole batch costs regardless of how many commands it carries.
/// </summary>
internal abstract class RoundTripCountingReaderBase(PostgresDataReaderProvider inner) : IDataReaderProvider, IRelationalCheckOps
{
    private int _roundTrips;

    public int RoundTripCount => Volatile.Read(ref _roundTrips);

    protected PostgresDataReaderProvider Inner { get; } = inner;

    protected void CountRoundTrip() => Interlocked.Increment(ref _roundTrips);

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetLatestSnapToken(cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetRelations(tupleFilter, cancellationToken);
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.HasDirectRelation(tupleFilter, subjectId, cancellationToken);
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation, string subjectId,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.HasAnyDirectRelation(entityType, entityIds, relation, subjectId, snapToken, cancellationToken);
    }

    public async Task<HashSet<string>> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.HasAnyOfDirectRelations(entityType, entityId, relationNames, subjectId, snapToken, cancellationToken);
    }

    public async Task<HashSet<string>> HasAnyOfAttributes(string entityType, string entityId, string[] attributeNames,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.HasAnyOfAttributes(entityType, entityId, attributeNames, snapToken, cancellationToken);
    }

    public async Task<bool> HasTupleToUserSetRelation(string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken,
        CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.HasTupleToUserSetRelation(entityType, entityId, tupleSetRelation, subEntityType,
            computedRelation, subjectType, subjectId, snapToken, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetIndirectRelations(tupleFilter, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter,
        string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetRelationsWithEntityIds(entityRelationFilter, subjectType, entityIds, subjectRelation, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
        string[] subjectsIds, string subjectType, EntityScope? scope, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetRelationsWithSubjectsIds(entityFilter, subjectsIds, subjectType, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIdsMultiRelation(string entityType,
        string[] relationNames, string[] subjectsIds, string subjectType, SnapToken snapToken, EntityScope? scope,
        CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetRelationsWithSubjectsIdsMultiRelation(entityType, relationNames, subjectsIds, subjectType,
            snapToken, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIdsMultiRelation(string entityType,
        string[] relationNames, string subjectType, IEnumerable<string> entityIds, string? subjectRelation,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetRelationsWithEntityIdsMultiRelation(entityType, relationNames, subjectType, entityIds,
            subjectRelation, snapToken, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(EntityRelationFilter mainFilter, string subEntityType,
        string subRelation, string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetRelationsJoined(mainFilter, subEntityType, subRelation, subjectType, subjectId, scope, cancellationToken);
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoinedByEntityIds(EntityRelationFilter mainFilter,
        IEnumerable<string> entityIds, string subEntityType, string subRelation, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetRelationsJoinedByEntityIds(mainFilter, entityIds, subEntityType, subRelation, cancellationToken);
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetAttribute(filter, cancellationToken);
    }

    public async Task<bool> HasTrueBoolAttribute(string entityType, string entityId, string attribute, SnapToken snapToken,
        CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.HasTrueBoolAttribute(entityType, entityId, attribute, snapToken, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetAttributes(filter, cancellationToken);
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(
        EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetAttributes(filter, scope, cancellationToken);
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetAttributesWithEntityIds(filter, entitiesIds, cancellationToken);
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(
        EntityAttributesFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetAttributesWithEntityIds(filter, entitiesIds, cancellationToken);
    }

    public async Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetAttributesSingleEntity(filter, cancellationToken);
    }

    public async Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetEntityIdsExcluding(entityType, excludeIds, snapToken, cancellationToken);
    }

    public async Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        CountRoundTrip();
        return await Inner.GetSubjectIdsExcluding(subjectType, excludeIds, snapToken, cancellationToken);
    }
}

/// <summary>
/// Implements <see cref="IRelationalBatchOps"/> so BatchedPhysicalExecutor's `Reader is
/// IRelationalBatchOps` check succeeds and a wave's batchable ops are packed into one
/// ExecuteBatchAsync round trip — the "batched" side of the Step 3 comparison.
/// </summary>
internal sealed class BatchingCountingReader(PostgresDataReaderProvider inner)
    : RoundTripCountingReaderBase(inner), IRelationalBatchOps
{
    // CreateBatch/AddXToBatch never touch the network — not counted, per IRelationalBatchOps'
    // own doc comments and this task's spec.
    public DbBatch CreateBatch() => Inner.CreateBatch();

    public void AddHasAnyOfDirectRelationsToBatch(DbBatch batch, string entityType, string entityId,
        string[] relationNames, string subjectId, SnapToken snapToken)
        => Inner.AddHasAnyOfDirectRelationsToBatch(batch, entityType, entityId, relationNames, subjectId, snapToken);

    public void AddHasAnyOfAttributesToBatch(DbBatch batch, string entityType, string entityId,
        string[] attributeNames, SnapToken snapToken)
        => Inner.AddHasAnyOfAttributesToBatch(batch, entityType, entityId, attributeNames, snapToken);

    public void AddHasDirectRelationToBatch(DbBatch batch, RelationTupleFilter tupleFilter, string subjectId)
        => Inner.AddHasDirectRelationToBatch(batch, tupleFilter, subjectId);

    public void AddHasTrueBoolAttributeToBatch(DbBatch batch, string entityType, string entityId, string attribute,
        SnapToken snapToken)
        => Inner.AddHasTrueBoolAttributeToBatch(batch, entityType, entityId, attribute, snapToken);

    public void AddHasTupleToUserSetRelationToBatch(DbBatch batch, string entityType, string entityId,
        string tupleSetRelation, string subEntityType, string computedRelation, string subjectType, string subjectId,
        SnapToken snapToken)
        => Inner.AddHasTupleToUserSetRelationToBatch(batch, entityType, entityId, tupleSetRelation, subEntityType,
            computedRelation, subjectType, subjectId, snapToken);

    public void AddHasAnyDirectRelationToBatch(DbBatch batch, string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken)
        => Inner.AddHasAnyDirectRelationToBatch(batch, entityType, entityIds, relation, subjectId, snapToken);

    public void AddGetRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
        => Inner.AddGetRelationsToBatch(batch, tupleFilter);

    public void AddGetIndirectRelationsToBatch(DbBatch batch, RelationTupleFilter tupleFilter)
        => Inner.AddGetIndirectRelationsToBatch(batch, tupleFilter);

    public async Task<DbDataReader> ExecuteBatchAsync(DbBatch batch, CancellationToken cancellationToken)
    {
        // One physical round trip regardless of how many commands `batch` carries.
        CountRoundTrip();
        return await Inner.ExecuteBatchAsync(batch, cancellationToken);
    }
}

/// <summary>
/// Deliberately does NOT implement <see cref="IRelationalBatchOps"/>: BatchedPhysicalExecutor's
/// `Reader is not IRelationalBatchOps` check fails for this type, so it falls back to
/// SubmitAllIndividually — the exact same per-op PhysicalOpRunner path DefaultPhysicalExecutor
/// itself uses (Valtuutus.Core.Engines.Check.V2.PhysicalOpRunner). This is the "individual round
/// trip per op" side of the Step 3 comparison. DefaultPhysicalExecutor itself is internal to
/// Valtuutus.Core with no InternalsVisibleTo grant to this test assembly, so it can't be
/// constructed directly here — this reader-capability toggle reaches the identical code path
/// without needing that grant.
/// </summary>
internal sealed class NonBatchingCountingReader(PostgresDataReaderProvider inner)
    : RoundTripCountingReaderBase(inner);
