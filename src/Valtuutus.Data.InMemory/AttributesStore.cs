using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Pools;

namespace Valtuutus.Data.InMemory;

public sealed class AttributesStore : IDisposable
{
    private sealed class Entry(AttributeTuple attribute, Ulid createdTxId)
    {
        public AttributeTuple Attribute { get; } = attribute;
        public Ulid CreatedTxId { get; } = createdTxId;
        public Ulid? DeletedTxId { get; set; }
    }

    private readonly Dictionary<(string, string), List<Entry>> _byEntityTypeAttr = new();
    private readonly Dictionary<string, List<Entry>> _byEntityType = new();
    private readonly List<Entry> _all = new();
    private readonly ReaderWriterLockSlim _rwls = new(LockRecursionPolicy.NoRecursion);

    private ReadScope Read() { _rwls.EnterReadLock(); return new ReadScope(_rwls); }
    private WriteScope Write() { _rwls.EnterWriteLock(); return new WriteScope(_rwls); }

    private static bool IsVisible(Entry e, in Ulid snapId)
    {
        return e.CreatedTxId.CompareTo(snapId) <= 0 &&
               (e.DeletedTxId is null || e.DeletedTxId.Value.CompareTo(snapId) > 0);
    }

    public AttributeTuple? GetAttribute(EntityAttributeFilter filter)
    {
        using var _ = Read();
        if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, filter.Attribute), out var bucket))
            return null;

        var snapId = Ulid.Parse(filter.SnapToken.Value);
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snapId)) continue;
            if (!string.IsNullOrWhiteSpace(filter.EntityId) && e.Attribute.EntityId != filter.EntityId) continue;
            return e.Attribute;
        }
        return null;
    }

    public bool HasTrueBoolAttribute(string entityType, string entityId, string attribute, SnapToken snap)
    {
        using var _ = Read();
        if (!_byEntityTypeAttr.TryGetValue((entityType, attribute), out var bucket))
            return false;
        var snapId = Ulid.Parse(snap.Value);
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snapId)) continue;
            if (e.Attribute.EntityId != entityId) continue;
            return e.Attribute.Value.TryGetValue(out bool b) && b;
        }
        return false;
    }

    public List<AttributeTuple> GetAttributes(EntityAttributeFilter filter)
    {
        using var _ = Read();
        if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, filter.Attribute), out var bucket))
            return [];

        var result = new List<AttributeTuple>(bucket.Count);
        var snapId = Ulid.Parse(filter.SnapToken.Value);
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snapId)) continue;
            if (!string.IsNullOrWhiteSpace(filter.EntityId) && e.Attribute.EntityId != filter.EntityId) continue;
            result.Add(e.Attribute);
        }
        return result;
    }

    public List<AttributeTuple> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entityIds)
    {
        var idSet = entityIds as ICollection<string> ?? entityIds.ToList();
        using var _ = Read();
        if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, filter.Attribute), out var bucket))
            return [];

        var result = new List<AttributeTuple>(idSet.Count);
        var snapId = Ulid.Parse(filter.SnapToken.Value);
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snapId)) continue;
            if (!idSet.Contains(e.Attribute.EntityId)) continue;
            result.Add(e.Attribute);
        }
        return result;
    }

    public Dictionary<(string, string), AttributeTuple> GetByNames(EntityAttributesFilter filter, HashSet<string>? scopedEntityIds = null)
    {
        using var _ = Read();
        var result = new Dictionary<(string, string), AttributeTuple>(filter.Attributes.Length);
        var snapId = Ulid.Parse(filter.SnapToken.Value);
        foreach (var attrName in filter.Attributes)
        {
            if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, attrName), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (!IsVisible(e, snapId)) continue;
                if (filter.EntityId is not null && e.Attribute.EntityId != filter.EntityId) continue;
                if (scopedEntityIds is not null && !scopedEntityIds.Contains(e.Attribute.EntityId)) continue;
                var k = (e.Attribute.Attribute, e.Attribute.EntityId);
                if (!result.ContainsKey(k)) result[k] = e.Attribute;
            }
        }
        return result;
    }

    public PooledList<AttributeTuple> GetByNamesSingleEntity(EntityAttributesFilter filter)
    {
        using var _ = Read();
        var result = PooledList<AttributeTuple>.Rent();
        var snapId = Ulid.Parse(filter.SnapToken.Value);
        foreach (var attrName in filter.Attributes)
        {
            if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, attrName), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (!IsVisible(e, snapId)) continue;
                if (filter.EntityId is not null && e.Attribute.EntityId != filter.EntityId) continue;
                result.Add(e.Attribute);
                break;
            }
        }
        return result;
    }

    public Dictionary<(string, string), AttributeTuple> GetByNamesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entityIds)
    {
        var idSet = entityIds as ICollection<string> ?? entityIds.ToList();
        using var _ = Read();
        var result = new Dictionary<(string, string), AttributeTuple>(filter.Attributes.Length);
        var snapId = Ulid.Parse(filter.SnapToken.Value);
        foreach (var attrName in filter.Attributes)
        {
            if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, attrName), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (!IsVisible(e, snapId)) continue;
                if (!idSet.Contains(e.Attribute.EntityId)) continue;
                var k = (e.Attribute.Attribute, e.Attribute.EntityId);
                if (!result.ContainsKey(k)) result[k] = e.Attribute;
            }
        }
        return result;
    }

    public void GetAllEntityIds(string entityType, SnapToken snap, HashSet<string> target)
    {
        using var _ = Read();
        if (!_byEntityType.TryGetValue(entityType, out var bucket)) return;
        var snapId = Ulid.Parse(snap.Value);
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snapId)) continue;
            target.Add(e.Attribute.EntityId);
        }
    }

    public void Write(Ulid transactId, IEnumerable<AttributeTuple> attributes)
    {
        var list = attributes as IList<AttributeTuple> ?? attributes.ToList();
        using var _ = Write();

        // Group incoming attributes by (EntityType, Attribute) so tombstoning only walks the
        // buckets this batch actually touches, instead of scanning every attribute ever written.
        var incomingByKey = new Dictionary<(string EntityType, string Attribute), HashSet<string>>();
        foreach (var a in list)
        {
            var key = (a.EntityType, a.Attribute);
            if (!incomingByKey.TryGetValue(key, out var entityIds))
                incomingByKey[key] = entityIds = new HashSet<string>();
            entityIds.Add(a.EntityId);
        }

        foreach (var (key, entityIds) in incomingByKey)
        {
            if (!_byEntityTypeAttr.TryGetValue(key, out var existingBucket)) continue;
            foreach (var existing in existingBucket)
            {
                if (existing.DeletedTxId is not null) continue;
                if (entityIds.Contains(existing.Attribute.EntityId))
                    existing.DeletedTxId = transactId;
            }
        }

        foreach (var a in list)
        {
            var entry = new Entry(a, transactId);
            var key = (a.EntityType, a.Attribute);
            if (!_byEntityTypeAttr.TryGetValue(key, out var bucket))
                _byEntityTypeAttr[key] = bucket = new List<Entry>();
            bucket.Add(entry);

            if (!_byEntityType.TryGetValue(a.EntityType, out var typeBucket))
                _byEntityType[a.EntityType] = typeBucket = new List<Entry>();
            typeBucket.Add(entry);

            _all.Add(entry);
        }
    }

    public void Delete(Ulid transactId, DeleteAttributesFilter[] filters)
    {
        using var _ = Write();
        foreach (var f in filters)
        {
            foreach (var e in _all)
            {
                if (e.DeletedTxId is not null) continue;
                if (!string.IsNullOrWhiteSpace(f.EntityId) && f.EntityId != e.Attribute.EntityId) continue;
                if (!string.IsNullOrWhiteSpace(f.EntityType) && f.EntityType != e.Attribute.EntityType) continue;
                if (!string.IsNullOrWhiteSpace(f.Attribute) && f.Attribute != e.Attribute.Attribute) continue;
                e.DeletedTxId = transactId;
            }
        }
    }

    public AttributeTuple[] Dump()
    {
        using var _ = Read();
        var result = new List<AttributeTuple>(_all.Count);
        foreach (var e in _all)
            if (e.DeletedTxId is null) result.Add(e.Attribute);
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
