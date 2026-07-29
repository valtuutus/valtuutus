# Storing Data

Valtuutus stores the data in a database of your preference, which serves as the single source of truth for all authorization queries and requests via the available engines.

In Valtuutus, you can store authorization data in two different forms: as relationships and as attributes.

Let’s examine relationships first.

## Relationships
In Valtuutus, relationships between your entities and objects build up a collection of access control lists (ACLs).

These ACLs are called relational tuples: the underlying data form that represents object-to-object and object-to-subject relations.
Each relational tuple represents an action that a specific entity can do on a resource and takes the form of entity U has relation R to object O, where entity U could be a simple user or a user set such as team X members.

## Attributes
Besides creating and storing your authorization-related data as relationships, you can also create attributes along with your resources and users.

For certain use cases, using relationships (ReBAC) or roles (RBAC) might not be the best fit. For example, geo-based permissions where access is granted only if associated with a geographical or regional attribute. Or consider time-based permissions, restricting certain actions to office hours. A simpler scenario involves defining certain individuals as banned, filtering them out from access despite meeting other requirements.

Attribute-Based Access Control takes a more contextual approach, allowing you to define access rights based on the context around subjects and objects in an application.

**Having said that, as of now, Valtuutus only supports boolean, string, integer, and decimal attributes.**

## Creating Authorization Data
Relationships and attributes can be created simply by calling the `IDataWriterProvider` or `IDbDataWriterProvider` function `Write`.
You can send multiple relations and attributes in a single call.

Each relational tuple or attribute should be created according to the schema you defined in the schema.

Let’s follow a simple document management system example with the following Valtuutus Schema to see how to create relation tuples.
```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity organization {
        relation admin @user;
        relation member @user;
    }
    entity document {
        relation owner @user;
        relation parent @organization;
        relation maintainer @user @organization#member;
        permission view := owner or parent.member or maintainer or parent.admin;
        permission edit := owner or maintainer or parent.admin;
        permission delete := owner or parent.admin;
""");
```

According to the schema above; when a user creates a document in an organization, more specifically let’s say, when user:1 create a document:2 we need to create the following relational tuple:
- document:2#owner@user:1

To create this relational tuple, you can call the `Write` function of the `IDataWriterProvider` as follows:

```csharp
await writer.Write([new RelationTuple("document", "2", "owner", "user", "1")], [], default);
```

If you want to pass a connection and a transaction as a parameter, just call the `Write` function of `IDbDataWriterProvider` as follows:

- No transaction
```csharp
await writer.Write(connection, [new RelationTuple("document", "2", "owner", "user", "1")], [], default);
```

- With transaction
```csharp
await writer.Write(connection, transaction, [new RelationTuple("document", "2", "owner", "user", "1")], [], default);
```

## Deleting Authorization Data

Relations and attributes can be deleted by calling `Delete` on `IDataWriterProvider` (or `IDbDataWriterProvider`). Like `Write`, `Delete` returns a `SnapToken` reflecting the state after the deletion.

```csharp
// Remove a specific relation tuple
await writer.Delete(
    new DeleteFilter
    {
        Relations =
        [
            new DeleteRelationsFilter
            {
                EntityType  = "document",
                EntityId    = "2",
                Relation    = "owner",
                SubjectType = "user",
                SubjectId   = "1"
            }
        ]
    },
    cancellationToken);
```

All fields on `DeleteRelationsFilter` are optional — only the filters you set are applied, making it easy to delete in bulk. For example, to remove all relations of a deleted document:

```csharp
await writer.Delete(
    new DeleteFilter
    {
        Relations = [new DeleteRelationsFilter { EntityType = "document", EntityId = "2" }]
    },
    cancellationToken);
```

Attributes can be deleted the same way using `DeleteAttributesFilter`:

```csharp
await writer.Delete(
    new DeleteFilter
    {
        Attributes =
        [
            new DeleteAttributesFilter
            {
                EntityType = "document",
                EntityId   = "2",
                Attribute  = "public"   // omit to delete all attributes for this entity
            }
        ]
    },
    cancellationToken);
```

You can mix relation and attribute deletions in a single `Delete` call by populating both `Relations` and `Attributes`.

If using `IDbDataWriterProvider`, pass a connection (and optionally a transaction) as the first parameters — the signature is identical to `Write`.

### How deletes work — soft deletes and data retention

`Delete` does **not** physically remove rows. It performs an `UPDATE` that sets the `deleted_tx_id` column on matching rows. The engines filter out soft-deleted rows on every read.

This means:
- Tables grow over time as data accumulates. Deleting a relation tuple does not reclaim disk space.
- Old soft-deleted rows must be periodically purged manually.

To reclaim space in Postgres, run a query like:

```sql
DELETE FROM relation_tuples WHERE deleted_tx_id IS NOT NULL;
DELETE FROM attributes       WHERE deleted_tx_id IS NOT NULL;
```

For SQL Server, the equivalent:

```sql
DELETE FROM relation_tuples WHERE deleted_tx_id IS NOT NULL;
DELETE FROM attributes       WHERE deleted_tx_id IS NOT NULL;
```

**These commands purge every tombstoned row unconditionally — there's no age filter.** A reader holding a snap token older than a given row's `deleted_tx_id` still needs that row to serve a consistent read; deleting it out from under that reader is exactly the kind of correctness break the soft-delete/snap-token scheme exists to prevent (see [Snap Tokens](#snap-tokens) below). Only run this as a one-off, e.g. during a maintenance window where you can guarantee no client is holding an old enough token. For ongoing, scheduled cleanup, use the [tombstone reaper](#tombstone-reaper) instead — it only ever removes tombstones older than a configurable retention window.

After a bulk purge in Postgres, run `VACUUM ANALYZE` on both tables to update statistics and reclaim pages.

How often to purge depends on your write volume. For high-throughput applications (frequent permission changes), use the tombstone reaper below for scheduled cleanup rather than scripting the raw commands above. For low-write applications, an occasional manual purge during a maintenance window is fine.

Passing around raw entity/relation/permission names as strings, and writing custom `fn` logic, can also be handled at build time — see [Source Generator](Source%20Generator.md).

### Snap Tokens

Every `Write` and `Delete` call returns a `SnapToken`:

```csharp
SnapToken token = await writer.Write(
    [new RelationTuple("document", "2", "owner", "user", "1")],
    [],
    cancellationToken);
// token.Value => "01J59G4294E1AR1AMCJTD0SPXW"
```

The token is a [ULID](https://github.com/ulid/spec) that encodes the point-in-time of the write. Pass it back on subsequent engine requests to guarantee **read-after-write consistency** — the engine will only read data at least as recent as that write:

```csharp
bool canView = await checkEngine.Check(
    new CheckRequest("document", "2", "view", "user", "1", snapToken: token),
    cancellationToken);
```

If you don't need strict consistency (e.g. read-heavy paths where eventual consistency is acceptable), omit the token entirely — the engine will use the latest available snapshot.

A common pattern is to store the token alongside the resource in your application database after a write, then read it back and pass it on the next authorization check for that resource.

### Tombstone Reaper

Soft-deleted rows are safe to keep around for a while — reads simply filter them out — but they still take up space and slow down scans if left forever. Instead of running the raw purge queries above, Valtuutus ships an opt-in tombstone reaper that removes tombstoned rows on a retention schedule, safely.

The reaper is opt-in, not automatic — nothing is deleted unless you register it and either call or schedule it yourself.

Register it with `AddTombstoneReaper` on your data builder:

```csharp
builder.Services
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!))
    .AddTombstoneReaper(options =>
    {
        options.RetentionPeriod = TimeSpan.FromDays(14);
    });
```

This registers `ITombstoneReaper`, which you can inject and call from any trigger you like — your own cron job, an admin endpoint, `pg_cron`, or anything else:

```csharp
public class PurgeJob(ITombstoneReaper reaper)
{
    public Task<ReapResult> RunAsync(CancellationToken cancellationToken) =>
        reaper.ReapAsync(cancellationToken);
}
```

If you'd rather not wire up your own trigger, add the `Valtuutus.Data.BackgroundService` package and call `AddTombstoneReaperHostedService()` to get a built-in timer that calls `ReapAsync` for you on an interval:

```csharp
builder.Services.AddTombstoneReaperHostedService();
```

This requires `AddTombstoneReaper` to already be registered — it throws at startup otherwise. The two approaches aren't exclusive; you can add the hosted service for routine sweeps and still call `ReapAsync` yourself for an on-demand run (e.g. from an admin endpoint).

`ValtuutusReaperOptions`, passed to `AddTombstoneReaper`, configures the reaper:
- `RetentionPeriod` (default 7 days) — tombstoned rows older than this are eligible for deletion.
- `BatchSize` (default 1000) — max rows deleted per table, per batch.
- `PauseBetweenBatches` (default 100ms) — delay between batches within one sweep.
- `SweepInterval` (default 1 hour) — only used by `AddTombstoneReaperHostedService`'s timer; ignored if you call `ReapAsync` yourself.

**The retention contract:** the reaper only ever deletes tombstoned rows (`deleted_tx_id` set) older than `RetentionPeriod`. A live row — one that was never deleted — is never touched, no matter how old it is; reads of live data are always safe regardless of what retention is set to.

`RetentionPeriod` only matters for reads presenting a snap token older than it. Those reads are served best-effort — whatever rows still happen to be there — not guaranteed complete, and they are **not** rejected with an error. This is deliberate: adding a per-read check for a stale token would put a permanent cost on every single `Check`/read call to guard a rare edge case, so there's no such check on the read path.

In practice, keep `RetentionPeriod` comfortably longer than the longest-lived snap token any client might realistically hold. The built-in `ResolveLatest` cache (used when you call `AddCaching`, see [Caching](Caching.md)) holds resolved snap tokens for 5 minutes — comfortably inside any sane retention default.

The `transactions` table is pruned differently: rows older than the watermark are removed by age alone, with no tombstone/live distinction (there's nothing to tombstone — it's an append-only log). This is safe: nothing on the read path joins back to `transactions` to decide whether a relation tuple or attribute is visible — visibility is decided purely by comparing `deleted_tx_id`/`created_tx_id` ULIDs as strings. The one consequence is that a still-live tuple's `created_at` audit metadata, if you look it up via its `created_tx_id`, can become unavailable once its `transactions` row ages out past the retention window — the tuple itself stays fully valid and readable either way.

