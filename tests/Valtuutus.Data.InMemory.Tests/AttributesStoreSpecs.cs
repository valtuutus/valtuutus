using System.Text.Json.Nodes;
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
}
