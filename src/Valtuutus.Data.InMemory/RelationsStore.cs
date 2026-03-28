using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Pools;

namespace Valtuutus.Data.InMemory;

internal sealed class RelationsStore : IDisposable
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

    private static bool IsVisible(Entry e, SnapToken? snap)
    {
        if (snap == null) return e.DeletedTxId is null;
        var id = Ulid.Parse(snap.Value.Value);
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

    public PooledList<RelationTuple> GetRelationsWithSubjectIds(EntityRelationFilter filter, IList<string> subjectIds, string subjectType)
    {
        using var _ = Read();
        if (!_byRelationSubjectType.TryGetValue((filter.EntityType, filter.Relation, subjectType), out var bucket))
            return PooledList<RelationTuple>.Rent();

        var result = PooledList<RelationTuple>.Rent();
        var snap = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (!subjectIds.Contains(e.Relation.SubjectId)) continue;
            result.Add(e.Relation);
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
