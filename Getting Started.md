# Getting Started

This guide takes you from zero to a working authorization check in under 10 minutes.

## 1. Install

Pick the storage backend that fits your stack:

```shell
# PostgreSQL
dotnet add package Valtuutus.Data.Postgres

# SQL Server
dotnet add package Valtuutus.Data.SqlServer

# In-memory (great for tests and local development)
dotnet add package Valtuutus.Data.InMemory
```

## 2. Run database migrations

If you're using a relational provider, create the required tables before starting. Run the SQL script for your database:

- [Postgres migration](src/Valtuutus.Data.Postgres/Database/migrations/20240221201712_initial.sql)
- [SqlServer migration](src/Valtuutus.Data.SqlServer/Database/migrations/20240224120604_initial.sql)

The InMemory provider needs no migration.

## 3. Register Valtuutus in DI

In `Program.cs` (or wherever you configure services), define your authorization schema and register a provider:

```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity document {
        relation owner @user;
        relation viewer @user;
        permission view := owner or viewer;
        permission edit := owner;
    }
""")
.AddPostgres(_ => () => new NpgsqlConnection(
    builder.Configuration.GetConnectionString("PostgresDb")!));
```

For SQL Server, replace `.AddPostgres(...)` with `.AddSqlServer(...)`. For InMemory:

```csharp
.AddInMemory()
```

## 4. Write authorization data

Inject `IDataWriterProvider` and write relation tuples that encode your access control facts. Here we say "user `alice` is the owner of document `readme`":

```csharp
public class DocumentService(IDataWriterProvider writer)
{
    public async Task CreateDocument(string documentId, string userId, CancellationToken ct)
    {
        // ... your document creation logic ...

        // Record the ownership relation in Valtuutus
        SnapToken token = await writer.Write(
            [new RelationTuple("document", documentId, "owner", "user", userId)],
            [],
            ct);

        // Store `token` alongside your resource if you need read-after-write consistency
    }
}
```

## 5. Check permissions

Inject `ICheckEngine` and ask whether a user can perform an action:

```csharp
public class DocumentController(ICheckEngine checkEngine) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

        bool canView = await checkEngine.Check(
            new CheckRequest("document", id, "view", "user", userId),
            ct);

        if (!canView)
            return Forbid();

        // ... return document ...
    }
}
```

## Next steps

- [Modeling Authorization](Modeling%20Authorization.md) — learn how to express complex RBAC, ABAC, and relationship-based policies in the schema DSL
- [Authorization Patterns](Authorization%20Patterns.md) — ready-made patterns for common use cases
- [Using the Engines](Using%20the%20Engines.md) — full reference for Check, SubjectPermission, LookupSubject, and LookupEntity
- [Storing Data](Storing%20Data.md) — writing, deleting, snap tokens, and the source generator
- [Caching](Caching.md) — reduce database load with FusionCache
