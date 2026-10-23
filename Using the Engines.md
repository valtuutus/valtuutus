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

## Upgrading from 1.6

- **Engine results carry the snap token they were read at.** `Check` returns a `CheckResult` (`Allowed`, `SnapToken`), `SubjectPermission` a `SubjectPermissionResult` (`Permissions`, `SnapToken`), and `Lookup` a `LookupSubjectResult` (`SubjectIds`, `SnapToken`). `LookupEntityPage` and `CheckExplainResult` gain a `SnapToken` property. Read `.Allowed`, `.Permissions` or `.SubjectIds` where you used the value directly.
- **Engine methods return `ValueTask`.** Await each call once; call `.AsTask()` when you need a `Task`, for example for `Task.WhenAll`.
- **Requests are immutable.** `SnapToken`, `Depth` and `Context` are `init`-only; use `with` to derive a request from another.
- **The previous recursive lookup engines are gone.** `AddValtuutusLookupSubjectV2()` and `AddValtuutusLookupEntityV2()` were removed, because `AddValtuutusCore` registers the graph/step engines itself. Delete those calls. See [How lookups run](#how-lookups-run).
- **Custom engines** (`ICheckEngine`, `ILookupEntityEngine`, `ILookupSubjectEngine` implementations) should report the token they read at. `AddCaching` caches a result under that token, and does not cache a result without one.

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
bool canView = (await checkEngine.Check(
    new CheckRequest("document", "2", "view", "user", "1", snapToken: token),
    cancellationToken)).Allowed;
```

When you don't need this guarantee (e.g. read-heavy queries where eventual consistency is acceptable), omit the token or pass `null`:

```csharp
bool canView = (await checkEngine.Check(
    new CheckRequest("document", "2", "view", "user", "1"),
    cancellationToken)).Allowed;
```

Every engine result reports the token it was read at. Pass it to a later request to read the same snapshot, for example to make a check and a lookup agree:

```csharp
CheckResult result = await checkEngine.Check(
    new CheckRequest("document", "2", "view", "user", "1"),
    cancellationToken);

var editors = await lookupSubjectEngine.Lookup(
    new LookupSubjectRequest("document", "edit", "user", "2") { SnapToken = result.SnapToken },
    cancellationToken);
```

The token is null only when the request carried none and the answer did not depend on any stored data.

---

## ICheckEngine

### Check — can entity U perform action Y on resource Z?

```csharp
bool canEdit = (await checkEngine.Check(
    new CheckRequest(
        entityType: "document",
        entityId:   "2",
        permission: "edit",
        subjectType: "user",
        subjectId:   "1"),
    cancellationToken)).Allowed;
```

### Check with context

When a permission expression references a function that reads `context.*` values, supply them via the `Context` dictionary:

```csharp
// Schema: permission push := owner or maintainer and notArchived(context.archived);
bool canPush = (await checkEngine.Check(
    new CheckRequest(
        entityType:  "repository",
        entityId:    "42",
        permission:  "push",
        subjectType: "user",
        subjectId:   "1",
        context: new Dictionary<string, object> { ["archived"] = false }),
    cancellationToken)).Allowed;
```

Pass the correct runtime type for each context value (`bool`, `int`, `string`, `decimal`) to avoid runtime errors.

### Explain — why was a permission granted or denied?

`Explain` walks the same graph as `Check` but returns a full resolution tree showing every evaluated relation, attribute, function, and expression — including which branches short-circuited and which results came from the memo cache.

```csharp
CheckExplainResult explain = await checkEngine.Explain(
    new CheckRequest(
        entityType:  "document",
        entityId:    "2",
        permission:  "edit",
        subjectType: "user",
        subjectId:   "1"),
    cancellationToken);

Console.WriteLine(explain.Result); // true / false

void Print(CheckNode node, int indent = 0)
{
    var prefix = new string(' ', indent * 2);
    Console.WriteLine($"{prefix}[{node.Type}] {node.Name} => {node.Result}" +
                      (node.Detail is not null ? $" ({node.Detail})" : ""));
    foreach (var child in node.Children)
        Print(child, indent + 1);
}

Print(explain.Root);
```

Example output for `edit := (owner or maintainer or parent.admin) and ...` where alice is a direct owner:

```
[Permission] edit => True
  [Expression] AND => True
    [Expression] OR => True
      [Relation] owner => True (direct tuple)
      [Relation] maintainer => False (skipped (evaluation stopped after a success))
      [TupleToUserSet] parent => False (skipped (evaluation stopped after a success))
    ...
```

#### Node types

| `CheckNodeType` | Meaning |
|---|---|
| `Permission` | A named permission or relation being evaluated |
| `Expression` | A binary `AND` / `OR` / `NOT` expression node |
| `Relation` | A direct relation tuple lookup |
| `TupleToUserSet` | An indirect lookup via a related entity (e.g. `parent.admin`) |
| `Attribute` | A boolean attribute lookup |
| `Function` | A custom `fn` call |

#### Detail strings

| Detail | Meaning |
|---|---|
| `"direct tuple"` | A matching relation tuple was found |
| `"no matching tuple"` | No relation tuple exists |
| `"attribute=True"` / `"attribute=False"` | The attribute value that was read |
| `"fn result=True"` / `"fn result=False"` | The return value of a custom function |
| `"memoized"` | Result was served from the per-request memo cache |
| `"skipped (evaluation stopped after a success)"` | Branch was not evaluated because an OR sibling already returned true |
| `"skipped (evaluation stopped after a failure)"` | Branch was not evaluated because an AND sibling already returned false |

> **Performance note:** `Explain` allocates a `CheckNode` tree on every call. Use `Check` on hot paths; reserve `Explain` for debugging, audit logging, or developer tooling.

### SubjectPermission — what permissions does user U have on resource Z?

Returns a dictionary of every permission defined on the entity and whether the subject has it:

```csharp
Dictionary<string, bool> permissions = (await checkEngine.SubjectPermission(
    new SubjectPermissionRequest
    {
        EntityType  = "document",
        EntityId    = "2",
        SubjectType = "user",
        SubjectId   = "1"
    },
    cancellationToken)).Permissions;

// permissions["view"]   => true
// permissions["edit"]   => true
// permissions["delete"] => false
```

---

## ILookupSubjectEngine

### Lookup — which subjects of type T have permission Y on entity X?

```csharp
HashSet<string> userIds = (await lookupSubjectEngine.Lookup(
    new LookupSubjectRequest(
        entityType:  "document",
        permission:  "view",
        subjectType: "user",
        entityId:    "2"),
    cancellationToken)).SubjectIds;

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

Results are ordered lexicographically by entity ID. Use `PageSize` and `ContinuationToken` to page through large result sets. Pass each page's `SnapToken` to the next request to read every page from the same snapshot; leave it unset to read each page at the latest one:

```csharp
string? continuationToken = null;
SnapToken? snapToken = null;
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
    snapToken = page.SnapToken;
} while (continuationToken is not null);
```

---

## How lookups run


- **They compile each permission once.** The plan for an (entity type, permission, subject type) is built on first use and reused by every request.
- **They read in steps.** All the queries a lookup can ask at the same time make up one step. On Postgres, SQL Server, MySQL and MariaDB, each step is a single database round trip.
- **They resolve the snap token in the first round trip.** A request without a `SnapToken` reads the latest one in its first round trip and uses it for every later step, so a write committed while the lookup runs stays invisible to it.
- **They walk recursive relations in fewer steps.** Examples are nested groups (`relation member @user @group#member`) or a userset chain.
  - On Postgres, SQL Server, MySQL and MariaDB, one query reads several levels of the chain.
  - The entity lookup stops as soon as a level finds nothing new, instead of continuing to the request's depth.

The number of levels one query reads is `LookupHopsPerQuery` on `ValtuutusPostgresOptions`, `ValtuutusSqlServerOptions` or `ValtuutusMySqlOptions`:

```csharp
.AddPostgres(
    _ => () => new NpgsqlConnection(connectionString),
    new ValtuutusPostgresOptions("public", "transactions", "relation_tuples", "attributes")
    {
        LookupHopsPerQuery = 3 // default 2; 1 reads one level per round trip
    });
```

Higher values save round trips on deep hierarchies, but each query gets larger. Keep the default unless your hierarchies are consistently deeper than two levels.

On MySQL and MariaDB the default is `12`. Their levels query stops at the first level that adds nothing, so a shallow hierarchy costs the same under a high value. Depth limits are unchanged: `Depth` still counts schema hops, whatever `LookupHopsPerQuery` is set to.

The InMemory provider runs the same engines, without the round-trip batching.

---

## Depth limit

All engine requests carry a `Depth` property (default: `10`). Each time the engine recurses into a nested relation or permission, it decrements the depth counter. When it reaches zero, the engine **treats that branch as `false`** and stops recursing — it does not throw an exception.

The depth counts traversal steps through the schema graph, not the number of tuples. A chain like `project → team → organization → user` consumes 3 depth units.

**When to increase it:**

Increase `Depth` if your schema has deeply nested hierarchies and you observe permissions returning `false` unexpectedly. As a rule of thumb, set it to at least the maximum number of relation hops in your longest permission chain, with some headroom:

```csharp
bool canView = (await checkEngine.Check(
    new CheckRequest("project", "proj-1", "view", "user", "alice")
    {
        Depth = 20   // increase from default 10 for deep hierarchies
    },
    cancellationToken)).Allowed;
```

**Setting a global default via `AddConcurrentQueryLimit`:**

The depth default is per-request. If you consistently need a higher value, set it on every request in a thin wrapper or middleware rather than relying on the default.

> **Note:** A very high depth limit on a complex schema with many tuples can cause fan-out into large numbers of concurrent queries. Use `AddConcurrentQueryLimit` to cap DB concurrency per request, and keep `Depth` as low as your schema requires.
