using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory;

internal sealed class AttributesStore : IDisposable
{
    private sealed class Entry(AttributeTuple attribute, Ulid createdTxId)
    {
        public AttributeTuple Attribute { get; } = attribute;
        public Ulid CreatedTxId { get; } = createdTxId;
        public Ulid? DeletedTxId { get; set; }
    }

    private readonly Dictionary<(string, string), List<Entry>> _byEntityTypeAttr = new();
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

    public AttributeTuple? GetAttribute(EntityAttributeFilter filter)
    {
        using var _ = Read();
        if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, filter.Attribute), out var bucket))
            return null;

        var snap = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (!string.IsNullOrWhiteSpace(filter.EntityId) && e.Attribute.EntityId != filter.EntityId) continue;
            return e.Attribute;
        }
        return null;
    }

    public List<AttributeTuple> GetAttributes(EntityAttributeFilter filter)
    {
        using var _ = Read();
        if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, filter.Attribute), out var bucket))
            return [];

        var result = new List<AttributeTuple>(bucket.Count);
        var snap = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snap)) continue;
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
        var snap = filter.SnapToken;
        foreach (var e in bucket)
        {
            if (!IsVisible(e, snap)) continue;
            if (!idSet.Contains(e.Attribute.EntityId)) continue;
            result.Add(e.Attribute);
        }
        return result;
    }

    public Dictionary<(string, string), AttributeTuple> GetByNames(EntityAttributesFilter filter, HashSet<string>? scopedEntityIds = null)
    {
        using var _ = Read();
        var result = new Dictionary<(string, string), AttributeTuple>(filter.Attributes.Length);
        var snap = filter.SnapToken;
        foreach (var attrName in filter.Attributes)
        {
            if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, attrName), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (!IsVisible(e, snap)) continue;
                if (scopedEntityIds is not null && !scopedEntityIds.Contains(e.Attribute.EntityId)) continue;
                var k = (e.Attribute.Attribute, e.Attribute.EntityId);
                if (!result.ContainsKey(k)) result[k] = e.Attribute;
            }
        }
        return result;
    }

    public Dictionary<(string, string), AttributeTuple> GetByNamesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entityIds)
    {
        var idSet = entityIds as ICollection<string> ?? entityIds.ToList();
        using var _ = Read();
        var result = new Dictionary<(string, string), AttributeTuple>(filter.Attributes.Length);
        var snap = filter.SnapToken;
        foreach (var attrName in filter.Attributes)
        {
            if (!_byEntityTypeAttr.TryGetValue((filter.EntityType, attrName), out var bucket)) continue;
            foreach (var e in bucket)
            {
                if (!IsVisible(e, snap)) continue;
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
        foreach (var e in _all)
        {
            if (e.Attribute.EntityType != entityType) continue;
            if (!IsVisible(e, snap)) continue;
            target.Add(e.Attribute.EntityId);
        }
    }

    public void Write(Ulid transactId, IEnumerable<AttributeTuple> attributes)
    {
        var list = attributes as IList<AttributeTuple> ?? attributes.ToList();
        using var _ = Write();
        foreach (var existing in _all)
        {
            if (existing.DeletedTxId is not null) continue;
            if (list.Any(a => a.EntityId == existing.Attribute.EntityId &&
                              a.EntityType == existing.Attribute.EntityType &&
                              a.Attribute == existing.Attribute.Attribute))
                existing.DeletedTxId = transactId;
        }
        foreach (var a in list)
        {
            var entry = new Entry(a, transactId);
            var key = (a.EntityType, a.Attribute);
            if (!_byEntityTypeAttr.TryGetValue(key, out var bucket))
                _byEntityTypeAttr[key] = bucket = new List<Entry>();
            bucket.Add(entry);
            _all.Add(entry);
        }
    }

    public void Delete(Ulid transactId, DeleteAttributesFilter[] filters)
    {
        using var _ = Write();
        foreach (var f in filters)
        foreach (var e in _all)
        {
            if (e.DeletedTxId is not null) continue;
            if (!string.IsNullOrWhiteSpace(f.EntityId) && f.EntityId != e.Attribute.EntityId) continue;
            if (!string.IsNullOrWhiteSpace(f.EntityType) && f.EntityType != e.Attribute.EntityType) continue;
            if (!string.IsNullOrWhiteSpace(f.Attribute) && f.Attribute != e.Attribute.Attribute) continue;
            e.DeletedTxId = transactId;
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
