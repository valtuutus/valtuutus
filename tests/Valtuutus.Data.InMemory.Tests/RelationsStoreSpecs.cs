using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class RelationsStoreSpecs
{
    private static SnapToken SnapAt(Ulid id) => new(id.ToString());

    // Ulid generation isn't guaranteed monotonic within the same tick, so order two freshly
    // generated values by comparison instead of relying on wall-clock gaps between writes.
    private static (Ulid earlier, Ulid later) OrderedPair()
    {
        var a = Ulid.NewUlid();
        var b = Ulid.NewUlid();
        return a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }

    [Fact]
    public void GetAllEntityIds_returns_only_ids_for_the_requested_entity_type()
    {
        using var store = new RelationsStore();
        var tx = Ulid.NewUlid();
        store.Write(tx, new[]
        {
            new RelationTuple("project", "p1", "owner", "user", "u1"),
            new RelationTuple("project", "p2", "owner", "user", "u2"),
            new RelationTuple("organization", "o1", "admin", "user", "u1"),
        });

        var result = store.GetAllEntityIds("project", SnapAt(tx));

        Assert.Equal(new HashSet<string> { "p1", "p2" }, result);
    }

    [Fact]
    public void GetAllEntityIds_excludes_tuples_created_after_the_snapshot()
    {
        using var store = new RelationsStore();
        var (tx1, tx2) = OrderedPair();
        store.Write(tx1, new[] { new RelationTuple("project", "p1", "owner", "user", "u1") });
        var snap = SnapAt(tx1);

        store.Write(tx2, new[] { new RelationTuple("project", "p2", "owner", "user", "u2") });

        var result = store.GetAllEntityIds("project", snap);

        Assert.Equal(new HashSet<string> { "p1" }, result);
    }

    [Fact]
    public void GetAllEntityIds_excludes_deleted_tuples()
    {
        using var store = new RelationsStore();
        var tx1 = Ulid.NewUlid();
        store.Write(tx1, new[] { new RelationTuple("project", "p1", "owner", "user", "u1") });

        var tx2 = Ulid.NewUlid();
        store.Delete(tx2, new[] { new DeleteRelationsFilter { EntityType = "project", EntityId = "p1" } });

        var result = store.GetAllEntityIds("project", SnapAt(tx2));

        Assert.Empty(result);
    }

    [Fact]
    public void GetAllSubjectIds_returns_only_direct_subjects_of_the_requested_type()
    {
        using var store = new RelationsStore();
        var tx = Ulid.NewUlid();
        store.Write(tx, new[]
        {
            new RelationTuple("project", "p1", "owner", "user", "u1"),
            new RelationTuple("project", "p1", "viewer", "group", "g1", "member"),
            new RelationTuple("organization", "o1", "admin", "user", "u2"),
        });

        var result = store.GetAllSubjectIds("user", SnapAt(tx));

        Assert.Equal(new HashSet<string> { "u1", "u2" }, result);
    }

    [Fact]
    public void GetRelationsWithSubjectIds_only_returns_requested_subject_ids()
    {
        using var store = new RelationsStore();
        var tx = Ulid.NewUlid();
        store.Write(tx, new[]
        {
            new RelationTuple("group", "g1", "member", "user", "u1"),
            new RelationTuple("group", "g1", "member", "user", "u2"),
            new RelationTuple("group", "g2", "member", "user", "u3"),
        });

        using var result = store.GetRelationsWithSubjectIds(
            new EntityRelationFilter { EntityType = "group", Relation = "member", SnapToken = SnapAt(tx) },
            new[] { "u1", "u3" }, "user");

        Assert.Equal(
            new HashSet<(string, string)> { ("g1", "u1"), ("g2", "u3") },
            result.Select(r => (r.EntityId, r.SubjectId)).ToHashSet());
    }
}
