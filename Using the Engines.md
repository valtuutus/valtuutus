# Using the Engines

This guide shows how to call each engine with real code examples. All examples use the following schema and assume it has been registered in DI:

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
    }
""");
```

Inject the engines through standard DI constructor injection:

```csharp
public class MyService(
    ICheckEngine checkEngine,
    ILookupSubjectEngine lookupSubjectEngine,
    ILookupEntityEngine lookupEntityEngine)
{ ... }
```

---

## Snap Tokens and Read Consistency

Every write operation returns a `SnapToken`:

```csharp
SnapToken token = await writer.Write(
    [new RelationTuple("document", "2", "owner", "user", "1")],
    [],
    cancellationToken);
```

Pass this token back in engine requests to guarantee **read-after-write consistency** — the engine will only read data at least as recent as the write that produced the token:

```csharp
bool canView = await checkEngine.Check(
    new CheckRequest("document", "2", "view", "user", "1", snapToken: token),
    cancellationToken);
```

When you don't need this guarantee (e.g. read-heavy queries where eventual consistency is acceptable), omit the token or pass `null`:

```csharp
bool canView = await checkEngine.Check(
    new CheckRequest("document", "2", "view", "user", "1"),
    cancellationToken);
```

---

## ICheckEngine

### Check — can entity U perform action Y on resource Z?

```csharp
bool canEdit = await checkEngine.Check(
    new CheckRequest(
        entityType: "document",
        entityId:   "2",
        permission: "edit",
        subjectType: "user",
        subjectId:   "1"),
    cancellationToken);
```

### Check with context

When a permission expression references a function that reads `context.*` values, supply them via the `Context` dictionary:

```csharp
// Schema: permission push := owner or maintainer and notArchived(context.archived);
bool canPush = await checkEngine.Check(
    new CheckRequest(
        entityType:  "repository",
        entityId:    "42",
        permission:  "push",
        subjectType: "user",
        subjectId:   "1",
        context: new Dictionary<string, object> { ["archived"] = false }),
    cancellationToken);
```

Pass the correct runtime type for each context value (`bool`, `int`, `string`, `decimal`) to avoid runtime errors.

### SubjectPermission — what permissions does user U have on resource Z?

Returns a dictionary of every permission defined on the entity and whether the subject has it:

```csharp
Dictionary<string, bool> permissions = await checkEngine.SubjectPermission(
    new SubjectPermissionRequest
    {
        EntityType  = "document",
        EntityId    = "2",
        SubjectType = "user",
        SubjectId   = "1"
    },
    cancellationToken);

// permissions["view"]   => true
// permissions["edit"]   => true
// permissions["delete"] => false
```

---

## ILookupSubjectEngine

### Lookup — which subjects of type T have permission Y on entity X?

```csharp
HashSet<string> userIds = await lookupSubjectEngine.Lookup(
    new LookupSubjectRequest(
        entityType:  "document",
        permission:  "view",
        subjectType: "user",
        entityId:    "2"),
    cancellationToken);

// userIds => { "1", "7", "42" }
```

> **Note:** For permissions that include `not()`, only subjects that already have at least one relation tuple in the data store are considered. See the [negation section](Modeling%20Authorization.md#negation) for details.

---

## ILookupEntityEngine

### LookupEntity — which resources of type T can subject U perform action Y on?

```csharp
LookupEntityPage page = await lookupEntityEngine.LookupEntity(
    new LookupEntityRequest(
        entityType:  "document",
        permission:  "view",
        subjectType: "user",
        subjectId:   "1"),
    cancellationToken);

// page.EntityIds         => ["2", "5", "9"]
// page.ContinuationToken => null  (no more pages)
```

### Scoped query — constrain to a parent entity

Use `EntityScope` to answer "which documents inside organization X can this user view?" — the same query you'd back a `GET /organizations/{orgId}/documents` endpoint with:

```csharp
LookupEntityPage page = await lookupEntityEngine.LookupEntity(
    new LookupEntityRequest("document", "view", "user", "1")
    {
        Scope = new EntityScope(
            Relation:    "parent",       // the relation on "document" that points to the parent
            SubjectType: "organization", // the parent entity type
            SubjectId:   "org-1"         // the specific parent to scope to
        )
    },
    cancellationToken);
```

### Pagination

Results are ordered lexicographically by entity ID. Use `PageSize` and `ContinuationToken` to page through large result sets. Reuse the same `SnapToken` across pages to ensure consistent results:

```csharp
string? continuationToken = null;
do
{
    var page = await lookupEntityEngine.LookupEntity(
        new LookupEntityRequest("document", "view", "user", "1")
        {
            Scope             = new EntityScope("parent", "organization", "org-1"),
            PageSize          = 50,
            ContinuationToken = continuationToken,
            SnapToken         = snapToken
        },
        cancellationToken);

    Process(page.EntityIds);
    continuationToken = page.ContinuationToken;
} while (continuationToken is not null);
```

---

## Depth limit

All engine requests carry a `Depth` property (default: `10`). Each time the engine recurses into a nested relation or permission, it decrements the depth counter. When it reaches zero, the engine **treats that branch as `false`** and stops recursing — it does not throw an exception.

The depth counts traversal steps through the schema graph, not the number of tuples. A chain like `project → team → organization → user` consumes 3 depth units.

**When to increase it:**

Increase `Depth` if your schema has deeply nested hierarchies and you observe permissions returning `false` unexpectedly. As a rule of thumb, set it to at least the maximum number of relation hops in your longest permission chain, with some headroom:

```csharp
bool canView = await checkEngine.Check(
    new CheckRequest("project", "proj-1", "view", "user", "alice")
    {
        Depth = 20   // increase from default 10 for deep hierarchies
    },
    cancellationToken);
```

**Setting a global default via `AddConcurrentQueryLimit`:**

The depth default is per-request. If you consistently need a higher value, set it on every request in a thin wrapper or middleware rather than relying on the default.

> **Note:** A very high depth limit on a complex schema with many tuples can cause fan-out into large numbers of concurrent queries. Use `AddConcurrentQueryLimit` to cap DB concurrency per request, and keep `Depth` as low as your schema requires.
