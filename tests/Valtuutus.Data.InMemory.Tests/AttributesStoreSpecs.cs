using System.Text.Json.Nodes;
using FluentAssertions;
using Valtuutus.Core;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class AttributesStoreSpecs
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

    private static AttributeTuple Attr(string entityType, string entityId, string attribute, int value = 1)
        => new(entityType, entityId, attribute, JsonValue.Create(value)!);

    private static AttributeTuple BoolAttr(string entityType, string entityId, string attribute, bool value)
        => new(entityType, entityId, attribute, JsonValue.Create(value)!);

    [Fact]
    public void GetAllEntityIds_returns_only_ids_for_the_requested_entity_type()
    {
        using var store = new AttributesStore();
        var tx = Ulid.NewUlid();
        store.Write(tx, new[]
        {
            Attr("project", "p1", "isActive"),
            Attr("project", "p2", "isActive"),
            Attr("organization", "o1", "isActive"),
        });

        var target = new HashSet<string>();
        store.GetAllEntityIds("project", SnapAt(tx), target);

        Assert.Equal(new HashSet<string> { "p1", "p2" }, target);
    }

    [Fact]
    public void GetAllEntityIds_excludes_tuples_created_after_the_snapshot()
    {
        using var store = new AttributesStore();
        var (tx1, tx2) = OrderedPair();
        store.Write(tx1, new[] { Attr("project", "p1", "isActive") });
        var snap = SnapAt(tx1);

        store.Write(tx2, new[] { Attr("project", "p2", "isActive") });

        var target = new HashSet<string>();
        store.GetAllEntityIds("project", snap, target);

        Assert.Equal(new HashSet<string> { "p1" }, target);
    }

    [Fact]
    public void GetAllEntityIds_excludes_deleted_tuples()
    {
        using var store = new AttributesStore();
        var (tx1, tx2) = OrderedPair();
        store.Write(tx1, new[] { Attr("project", "p1", "isActive") });

        store.Delete(tx2, new[] { new DeleteAttributesFilter { EntityType = "project", EntityId = "p1" } });

        var target = new HashSet<string>();
        store.GetAllEntityIds("project", SnapAt(tx2), target);

        Assert.Empty(target);
    }

    [Fact]
    public void Write_supersedes_prior_value_for_the_same_entity_and_attribute()
    {
        using var store = new AttributesStore();
        var (tx1, tx2) = OrderedPair();
        store.Write(tx1, new[] { Attr("project", "p1", "isActive", 1) });
        store.Write(tx2, new[] { Attr("project", "p1", "isActive", 2) });

        var current = store.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "project", EntityId = "p1", Attribute = "isActive", SnapToken = SnapAt(tx2)
        });

        Assert.Equal(2, current!.Value.GetValue<int>());

        // The superseded value must be invisible at the later snapshot too — not just replaced going forward.
        var all = store.GetAttributes(new EntityAttributeFilter
        {
            EntityType = "project", EntityId = "p1", Attribute = "isActive", SnapToken = SnapAt(tx2)
        });
        Assert.Single(all);
    }

    [Fact]
    public void Write_does_not_supersede_the_same_attribute_on_a_different_entity()
    {
        using var store = new AttributesStore();
        var (tx1, tx2) = OrderedPair();
        store.Write(tx1, new[] { Attr("project", "p1", "isActive", 1) });
        store.Write(tx2, new[] { Attr("project", "p2", "isActive", 2) });

        var p1 = store.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "project", EntityId = "p1", Attribute = "isActive", SnapToken = SnapAt(tx2)
        });

        Assert.Equal(1, p1!.Value.GetValue<int>());
    }

    [Fact]
    public void Write_does_not_supersede_a_different_attribute_on_the_same_entity()
    {
        using var store = new AttributesStore();
        var (tx1, tx2) = OrderedPair();
        store.Write(tx1, new[] { Attr("project", "p1", "isActive", 1) });
        store.Write(tx2, new[] { Attr("project", "p1", "isPublic", 1) });

        var isActive = store.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "project", EntityId = "p1", Attribute = "isActive", SnapToken = SnapAt(tx2)
        });

        Assert.NotNull(isActive);
    }

    [Fact]
    public void Write_supersedes_correct_entity_when_batch_touches_multiple_entity_ids_for_the_same_attribute()
    {
        using var store = new AttributesStore();
        var (tx1, tx2) = OrderedPair();
        store.Write(tx1, new[]
        {
            Attr("project", "p1", "isActive", 1),
            Attr("project", "p2", "isActive", 1),
        });

        store.Write(tx2, new[] { Attr("project", "p1", "isActive", 2) });

        var p1 = store.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "project", EntityId = "p1", Attribute = "isActive", SnapToken = SnapAt(tx2)
        });
        var p2 = store.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "project", EntityId = "p2", Attribute = "isActive", SnapToken = SnapAt(tx2)
        });

        Assert.Equal(2, p1!.Value.GetValue<int>());
        Assert.Equal(1, p2!.Value.GetValue<int>());
    }

    [Fact]
    public void HasTrueBoolAttribute_true_value_returns_true()
    {
        using var store = new AttributesStore();
        var tx = Ulid.NewUlid();
        store.Write(tx, new[] { BoolAttr("project", "p1", "public", true) });

        Assert.True(store.HasTrueBoolAttribute("project", "p1", "public", SnapAt(tx)));
    }

    [Fact]
    public void HasTrueBoolAttribute_false_value_returns_false()
    {
        using var store = new AttributesStore();
        var tx = Ulid.NewUlid();
        store.Write(tx, new[] { BoolAttr("project", "p1", "public", false) });

        Assert.False(store.HasTrueBoolAttribute("project", "p1", "public", SnapAt(tx)));
    }

    [Fact]
    public void HasTrueBoolAttribute_missing_attribute_returns_false()
    {
        using var store = new AttributesStore();
        var tx = Ulid.NewUlid();
        store.Write(tx, new[] { BoolAttr("project", "p1", "public", true) });

        Assert.False(store.HasTrueBoolAttribute("project", "p2", "public", SnapAt(tx)));
        Assert.False(store.HasTrueBoolAttribute("project", "p1", "archived", SnapAt(tx)));
    }

    [Fact]
    public void GetByNamesWithEntityIds_only_returns_requested_entity_ids()
    {
        using var store = new AttributesStore();
        var tx = Ulid.NewUlid();
        store.Write(tx, new[]
        {
            BoolAttr("project", "p1", "public", true),
            BoolAttr("project", "p2", "public", true),
            BoolAttr("project", "p3", "public", true),
        });

        var result = store.GetByNamesWithEntityIds(
            new EntityAttributesFilter
            {
                EntityType = "project", Attributes = new[] { "public" }, SnapToken = SnapAt(tx)
            },
            new[] { "p1", "p3" });

        Assert.Equal(
            new HashSet<(string, string)> { ("public", "p1"), ("public", "p3") },
            result.Keys.ToHashSet());
    }

    [Fact]
    public void HasTrueBoolAttribute_respects_snap_visibility()
    {
        using var store = new AttributesStore();
        var (tx1, tx2) = OrderedPair();
        store.Write(tx2, new[] { BoolAttr("project", "p1", "public", true) });

        Assert.False(store.HasTrueBoolAttribute("project", "p1", "public", SnapAt(tx1)));
        Assert.True(store.HasTrueBoolAttribute("project", "p1", "public", SnapAt(tx2)));
    }

    [Fact]
    public void ReapTombstones_removes_entries_tombstoned_before_the_watermark()
    {
        using var store = new AttributesStore();
        // OrderedPair guarantees tx2 > tx1 — required below so the pre-delete snapshot check
        // is actually meaningful (see comment further down in this test).
        var (tx1, tx2) = OrderedPair();
        store.Write(tx1, new[] { new AttributeTuple("project", "p1", "name", System.Text.Json.Nodes.JsonValue.Create("a")!) });
        store.Delete(tx2, new[] { new DeleteAttributesFilter { EntityType = "project", EntityId = "p1" } });

        var watermark = Ulid.NewUlid(DateTimeOffset.UtcNow.AddSeconds(1));
        var removed = store.ReapTombstones(watermark);

        removed.Should().Be(1);
        store.Dump().Should().BeEmpty();

        // Query with a snapshot from BEFORE the delete (tx1). Under IsVisible's own rules, an
        // entry created at-or-before tx1 and deleted strictly after tx1 (tx2 > tx1, guaranteed
        // by OrderedPair) would still count as visible at this snapshot if it still physically
        // existed in a bucket — IsVisible only checks DeletedTxId > snapId, which holds here.
        // So the only way these assertions can pass is if ReapTombstones truly removed the
        // entry from the secondary index buckets, not just tombstoned it logically. Checking
        // both _byEntityTypeAttr (GetAttribute) and _byEntityType (GetAllEntityIds) covers
        // both buckets RemoveFromBuckets is called against.
        var preDeleteSnap = SnapAt(tx1);
        store.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "project", EntityId = "p1", Attribute = "name", SnapToken = preDeleteSnap
        }).Should().BeNull();

        var target = new HashSet<string>();
        store.GetAllEntityIds("project", preDeleteSnap, target);
        target.Should().BeEmpty();
    }

    [Fact]
    public void ReapTombstones_never_removes_a_live_entry_regardless_of_its_age()
    {
        using var store = new AttributesStore();
        var tx1 = Ulid.NewUlid();
        store.Write(tx1, new[] { new AttributeTuple("project", "p1", "name", System.Text.Json.Nodes.JsonValue.Create("a")!) });

        var farFutureWatermark = Ulid.NewUlid(DateTimeOffset.UtcNow.AddYears(10));
        var removed = store.ReapTombstones(farFutureWatermark);

        removed.Should().Be(0);
        store.Dump().Should().ContainSingle(a => a.EntityId == "p1");
    }

    [Fact]
    public void ReapTombstones_keeps_a_tombstone_exactly_at_the_watermark()
    {
        using var store = new AttributesStore();
        var tx1 = Ulid.NewUlid();
        store.Write(tx1, new[] { new AttributeTuple("project", "p1", "name", System.Text.Json.Nodes.JsonValue.Create("a")!) });
        var tx2 = Ulid.NewUlid();
        store.Delete(tx2, new[] { new DeleteAttributesFilter { EntityType = "project", EntityId = "p1" } });

        // watermark == tx2 exactly: only strictly-older tombstones are eligible
        var removed = store.ReapTombstones(tx2);

        removed.Should().Be(0);
    }
}
