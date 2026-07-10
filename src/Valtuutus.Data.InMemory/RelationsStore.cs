using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Pools;

namespace Valtuutus.Data.InMemory;

public sealed class RelationsStore : IDisposable
{
    private sealed class Entry(RelationTuple relation, Ulid createdTxId)
    {
        public RelationTuple Relation { get; } = relation;
        public Ulid CreatedTxId { get; } = createdTxId;
        public Ulid? DeletedTxId { get; set; }
    }

    private readonly Dictionary<(string, string, string), List<Entry>> _byEntityRelation = new();
    private readonly Dictionary<(string, string, string), List<Entry>> _byRelationSubjectType = new();
    private readonly List<Entry> _all = new();
    private readonly ReaderWriterLockSlim _rwls = new(LockRecursionPolicy.NoRecursion);

    private ReadScope Read() { _rwls.EnterReadLock(); return new ReadScope(_rwls); }
    private WriteScope Write() { _rwls.EnterWriteLock(); return new WriteScope(_rwls); }

    private static bool IsVisible(Entry e, SnapToken snap)
    {
        var id = Ulid.Parse(snap.Value);
        return e.CreatedTxId.CompareTo(id) <= 0 &&
               (e.DeletedTxId is null || e.DeletedTxId.Value.CompareTo(id) > 0);
    }

    public PooledList<RelationTuple> GetRelations(RelationTupleFilter filter)
    {
        using var _ = Read();
        if (!_byEntityRelation.TryGetValue((filter.EntityType, filter.EntityId, filter.Relation), out var bucket))
            return PooledList<RelationTuple>.Rent();

        var result = PooledList<RelationTuple>.Rent();
        var snap = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (!string.IsNullOrEmpty(filter.SubjectId) && e.Relation.SubjectId != filter.SubjectId) continue;
            if (!string.IsNullOrEmpty(filter.SubjectRelation) && e.Relation.SubjectRelation != filter.SubjectRelation) continue;
            if (!string.IsNullOrEmpty(filter.SubjectType) && e.Relation.SubjectType != filter.SubjectType) continue;
            result.Add(e.Relation);
        }
        return result;
    }

    public bool HasDirectRelation(RelationTupleFilter filter, string subjectId)
    {
        using var _ = Read();
        if (!_byEntityRelation.TryGetValue((filter.EntityType, filter.EntityId, filter.Relation), out var bucket))
            return false;
        var snap = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (e.Relation.SubjectId != subjectId) continue;
            if (!string.IsNullOrEmpty(e.Relation.SubjectRelation)) continue;
            return true;
        }
        return false;
    }

    public PooledList<RelationTuple> GetIndirectRelations(RelationTupleFilter filter)
    {
        using var _ = Read();
        if (!_byEntityRelation.TryGetValue((filter.EntityType, filter.EntityId, filter.Relation), out var bucket))
            return PooledList<RelationTuple>.Rent();
        var result = PooledList<RelationTuple>.Rent();
        var snap = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (string.IsNullOrEmpty(e.Relation.SubjectRelation)) continue;
            result.Add(e.Relation);
        }
        return result;
    }

    public bool HasAnyDirectRelation(string entityType, string[] entityIds, string relation, string subjectId, SnapToken snapToken)
    {
        using var _ = Read();
        foreach (var entityId in entityIds)
        {
            if (!_byEntityRelation.TryGetValue((entityType, entityId, relation), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (!IsVisible(e, snapToken)) continue;
                if (e.Relation.SubjectId != subjectId) continue;
                if (!string.IsNullOrEmpty(e.Relation.SubjectRelation)) continue;
                return true;
            }
        }
        return false;
    }

    public HashSet<string> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames, string subjectId, SnapToken snapToken)
    {
        using var _ = Read();
        var result = new HashSet<string>(relationNames.Length, StringComparer.Ordinal);
        foreach (var relationName in relationNames)
        {
            if (!_byEntityRelation.TryGetValue((entityType, entityId, relationName), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (!IsVisible(e, snapToken)) continue;
                if (e.Relation.SubjectId != subjectId) continue;
                if (!string.IsNullOrEmpty(e.Relation.SubjectRelation)) continue;
                result.Add(relationName);
                break;
            }
        }
        return result;
    }

    public bool HasTupleToUserSetRelation(
        string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken)
    {
        using var _ = Read();
        if (!_byEntityRelation.TryGetValue((entityType, entityId, tupleSetRelation), out var bucket))
            return false;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snapToken)) continue;
            if (!string.IsNullOrEmpty(e.Relation.SubjectRelation)) continue;
            if (e.Relation.SubjectType != subEntityType) continue;
            if (!_byEntityRelation.TryGetValue((subEntityType, e.Relation.SubjectId, computedRelation), out var depBucket))
                continue;
            foreach (var dep in depBucket)
            {
                if (!IsVisible(dep, snapToken)) continue;
                if (!string.IsNullOrEmpty(dep.Relation.SubjectRelation)) continue;
                if (dep.Relation.SubjectType == subjectType && dep.Relation.SubjectId == subjectId)
                    return true;
            }
        }
        return false;
    }

    public PooledList<RelationTuple> GetRelationsWithEntityIds(EntityRelationFilter filter, string subjectType,
        IEnumerable<string> entityIds, string? subjectRelation)
    {
        var idSet = entityIds as ICollection<string> ?? entityIds.ToList();
        using var _ = Read();
        if (!_byRelationSubjectType.TryGetValue((filter.EntityType, filter.Relation, subjectType), out var bucket))
            return PooledList<RelationTuple>.Rent();

        var result = PooledList<RelationTuple>.Rent();
        var snap = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (!idSet.Contains(e.Relation.EntityId)) continue;
            if (!string.IsNullOrEmpty(subjectRelation) && e.Relation.SubjectRelation != subjectRelation) continue;
            result.Add(e.Relation);
        }
        return result;
    }

    public PooledList<RelationTuple> GetRelationsWithSubjectIds(EntityRelationFilter filter, string[] subjectIds, string subjectType, EntityScope? scope = null)
    {
        using var _ = Read();
        if (!_byRelationSubjectType.TryGetValue((filter.EntityType, filter.Relation, subjectType), out var bucket))
            return PooledList<RelationTuple>.Rent();

        // If scope is set, build a HashSet of entity IDs that satisfy the scope relation
        HashSet<string>? scopedEntityIds = null;
        if (scope.HasValue)
        {
            var s = scope.Value;
            if (_byRelationSubjectType.TryGetValue((filter.EntityType, s.Relation, s.SubjectType), out var scopeBucket))
            {
                scopedEntityIds = new HashSet<string>();
                var snap = filter.SnapToken;
                foreach (var e in scopeBucket)
                {
                    if (!IsVisible(e, snap)) continue;
                    if (e.Relation.SubjectId != s.SubjectId) continue;
                    scopedEntityIds.Add(e.Relation.EntityId);
                }
            }
            if (scopedEntityIds is null || scopedEntityIds.Count == 0)
                return PooledList<RelationTuple>.Rent();
        }

        var result = PooledList<RelationTuple>.Rent();
        var snapToken = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snapToken)) continue;
            if (!subjectIds.Contains(e.Relation.SubjectId)) continue;
            if (scopedEntityIds is not null && !scopedEntityIds.Contains(e.Relation.EntityId)) continue;
            result.Add(e.Relation);
        }
        return result;
    }

    public PooledList<RelationTuple> GetRelationsJoined(
        EntityRelationFilter mainFilter, string subEntityType, string subRelation,
        string subjectType, string subjectId, EntityScope? scope = null)
    {
        using var _ = Read();
        var snap = mainFilter.SnapToken;

        // Step 1: collect intermediate IDs — sub-entities that have the subject.
        if (!_byRelationSubjectType.TryGetValue((subEntityType, subRelation, subjectType), out var depBucket))
            return PooledList<RelationTuple>.Rent();

        HashSet<string>? intermediateIds = null;
        foreach (var e in depBucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (e.Relation.SubjectId != subjectId) continue;
            (intermediateIds ??= new HashSet<string>()).Add(e.Relation.EntityId);
        }

        if (intermediateIds is null || intermediateIds.Count == 0)
            return PooledList<RelationTuple>.Rent();

        HashSet<string>? scopedEntityIds = null;
        if (scope.HasValue)
        {
            var s = scope.Value;
            if (_byRelationSubjectType.TryGetValue((mainFilter.EntityType, s.Relation, s.SubjectType), out var scopeBucket))
            {
                scopedEntityIds = new HashSet<string>();
                foreach (var e in scopeBucket)
                {
                    if (!IsVisible(e, snap)) continue;
                    if (e.Relation.SubjectId != s.SubjectId) continue;
                    scopedEntityIds.Add(e.Relation.EntityId);
                }
            }
            if (scopedEntityIds is null || scopedEntityIds.Count == 0)
                return PooledList<RelationTuple>.Rent();
        }

        // Step 2: find main entities whose subject is in the intermediate set.
        if (!_byRelationSubjectType.TryGetValue((mainFilter.EntityType, mainFilter.Relation, subEntityType), out var mainBucket))
            return PooledList<RelationTuple>.Rent();

        var result = PooledList<RelationTuple>.Rent();
        foreach (var e in mainBucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (!intermediateIds.Contains(e.Relation.SubjectId)) continue;
            if (scopedEntityIds is not null && !scopedEntityIds.Contains(e.Relation.EntityId)) continue;
            result.Add(e.Relation);
        }
        return result;
    }

    public PooledList<RelationTuple> GetRelationsJoinedByEntityIds(
        EntityRelationFilter mainFilter, IEnumerable<string> entityIds, string subEntityType, string subRelation)
    {
        using var _ = Read();
        var snap = mainFilter.SnapToken;

        // Step 1: collect intermediate IDs — subEntityType-typed subjects reachable from entityIds.
        if (!_byRelationSubjectType.TryGetValue((mainFilter.EntityType, mainFilter.Relation, subEntityType), out var depBucket))
            return PooledList<RelationTuple>.Rent();

        var idSet = entityIds as ICollection<string> ?? entityIds.ToList();
        HashSet<string>? intermediateIds = null;
        foreach (var e in depBucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (!idSet.Contains(e.Relation.EntityId)) continue;
            (intermediateIds ??= new HashSet<string>()).Add(e.Relation.SubjectId);
        }

        if (intermediateIds is null || intermediateIds.Count == 0)
            return PooledList<RelationTuple>.Rent();

        // Step 2: find dependent tuples for each intermediate entity.
        var result = PooledList<RelationTuple>.Rent();
        foreach (var id in intermediateIds)
        {
            if (!_byEntityRelation.TryGetValue((subEntityType, id, subRelation), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (!IsVisible(e, snap)) continue;
                result.Add(e.Relation);
            }
        }
        return result;
    }

    public HashSet<string> GetAllEntityIds(string entityType, SnapToken snap)
    {
        using var _ = Read();
        var result = new HashSet<string>();
        foreach (var e in _all)
        {
            if (e.Relation.EntityType != entityType) continue;
            if (!IsVisible(e, snap)) continue;
            result.Add(e.Relation.EntityId);
        }
        return result;
    }

    public HashSet<string> GetAllSubjectIds(string subjectType, SnapToken snap)
    {
        using var _ = Read();
        var result = new HashSet<string>();
        foreach (var e in _all)
        {
            if (e.Relation.SubjectType != subjectType) continue;
            if (!e.Relation.IsDirectSubject()) continue;
            if (!IsVisible(e, snap)) continue;
            result.Add(e.Relation.SubjectId);
        }
        return result;
    }

    public void Write(Ulid transactId, IEnumerable<RelationTuple> relations)
    {
        using var _ = Write();
        foreach (var r in relations)
        {
            var entry = new Entry(r, transactId);

            var pk = (r.EntityType, r.EntityId, r.Relation);
            if (!_byEntityRelation.TryGetValue(pk, out var b1))
                _byEntityRelation[pk] = b1 = new List<Entry>();
            b1.Add(entry);

            var sk = (r.EntityType, r.Relation, r.SubjectType);
            if (!_byRelationSubjectType.TryGetValue(sk, out var b2))
                _byRelationSubjectType[sk] = b2 = new List<Entry>();
            b2.Add(entry);

            _all.Add(entry);
        }
    }

    public void Delete(Ulid transactId, DeleteRelationsFilter[] filters)
    {
        using var _ = Write();
        foreach (var f in filters)
        {
            foreach (var e in _all)
            {
                if (e.DeletedTxId is not null) continue;
                if (!string.IsNullOrWhiteSpace(f.EntityId) && f.EntityId != e.Relation.EntityId) continue;
                if (!string.IsNullOrWhiteSpace(f.EntityType) && f.EntityType != e.Relation.EntityType) continue;
                if (!string.IsNullOrWhiteSpace(f.SubjectId) && f.SubjectId != e.Relation.SubjectId) continue;
                if (!string.IsNullOrWhiteSpace(f.SubjectType) && f.SubjectType != e.Relation.SubjectType) continue;
                if (!string.IsNullOrWhiteSpace(f.SubjectRelation) && f.SubjectRelation != e.Relation.SubjectRelation) continue;
                if (!string.IsNullOrWhiteSpace(f.Relation) && f.Relation != e.Relation.Relation) continue;
                e.DeletedTxId = transactId;
            }
        }
    }

    public RelationTuple[] Dump()
    {
        using var _ = Read();
        var result = new List<RelationTuple>(_all.Count);
        foreach (var e in _all)
            if (e.DeletedTxId is null) result.Add(e.Relation);
        return result.ToArray();
    }

    public void Dispose() => _rwls.Dispose();

    private readonly struct ReadScope(ReaderWriterLockSlim rwls) : IDisposable
    {
        public void Dispose() => rwls.ExitReadLock();
    }

    private readonly struct WriteScope(ReaderWriterLockSlim rwls) : IDisposable
    {
        public void Dispose() => rwls.ExitWriteLock();
    }
}
