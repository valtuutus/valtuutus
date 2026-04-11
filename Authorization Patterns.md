# Authorization Patterns

This page shows ready-made patterns for common authorization scenarios. Each pattern includes a complete schema, the relation tuples you'd write, and the checks you'd run.

---

## Role-Based Access Control (RBAC)

Roles are modeled as relations. Permissions are union expressions over those relations.

```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity organization {
        relation admin @user;
        relation member @user;
        relation guest @user;

        permission manage_billing := admin;
        permission invite_member  := admin or member;
        permission view_resources := admin or member or guest;
    }
""");
```

**Write tuples:**
```csharp
// alice is an admin of org-1
await writer.Write([
    new RelationTuple("organization", "org-1", "admin",  "user", "alice"),
    new RelationTuple("organization", "org-1", "member", "user", "bob"),
    new RelationTuple("organization", "org-1", "guest",  "user", "carol"),
], [], ct);
```

**Check:**
```csharp
// true — alice is admin
await checkEngine.Check(new CheckRequest("organization", "org-1", "manage_billing", "user", "alice"), ct);

// false — bob is only a member
await checkEngine.Check(new CheckRequest("organization", "org-1", "manage_billing", "user", "bob"), ct);
```

---

## Hierarchical RBAC

Permissions propagate through parent-child relationships. A user who is admin of an organization automatically has elevated rights on any resource that inherits from it.

```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity organization {
        relation admin @user;
        relation member @user;
    }
    entity team {
        relation parent @organization;
        relation owner @user;
        relation member @user;

        permission manage := owner or parent.admin;
        permission view   := member or owner or parent.admin or parent.member;
    }
    entity project {
        relation parent @team;
        relation contributor @user;

        // parent.manage and parent.view already encode the full team + org hierarchy
        permission edit := contributor or parent.manage;
        permission view := contributor or parent.view;
    }
""");
```

The dot notation resolves permissions, not just relations. `parent.manage` on `project` walks to the linked `team` and evaluates its `manage` permission — which itself already encodes `owner or parent.admin`. The full org → team → project hierarchy is expressed without repeating any logic.

**Write tuples:**
```csharp
await writer.Write([
    new RelationTuple("organization", "org-1", "admin",  "user", "alice"),
    new RelationTuple("team",         "team-1", "parent", "organization", "org-1"),
    new RelationTuple("team",         "team-1", "member", "user", "bob"),
    new RelationTuple("project",      "proj-1", "parent", "team", "team-1"),
], [], ct);
```

**Check:**
```csharp
// true — alice is org admin, which propagates through team-1 to proj-1
await checkEngine.Check(new CheckRequest("project", "proj-1", "edit", "user", "alice"), ct);

// true — bob is a team member
await checkEngine.Check(new CheckRequest("project", "proj-1", "view", "user", "bob"), ct);
```

---

## Attribute-Based Access Control (ABAC)

Attributes let you encode properties of entities (public/private, status, region) and use them directly in permission expressions.

```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity document {
        relation owner @user;
        attribute public bool;
        attribute status string;

        // anyone can view public documents; only owners can view private ones
        permission view := owner or public;

        // edit requires ownership AND the document must be in draft status
        permission edit := owner and isDraft(status);
    }

    fn isDraft(status string) => status == "draft";
""");
```

**Write tuples and attributes:**
```csharp
await writer.Write(
    [new RelationTuple("document", "doc-1", "owner", "user", "alice")],
    [
        new AttributeTuple("document", "doc-1", "public", JsonValue.Create(false)),
        new AttributeTuple("document", "doc-1", "status", JsonValue.Create("draft")),
    ],
    ct);
```

**Check:**
```csharp
// true — alice is owner and doc is draft
await checkEngine.Check(new CheckRequest("document", "doc-1", "edit", "user", "alice"), ct);

// false — bob is not owner and doc is not public
await checkEngine.Check(new CheckRequest("document", "doc-1", "view", "user", "bob"), ct);
```

> **Note:** If an attribute tuple does not exist for an entity, any attribute check in a permission expression evaluates to `false`.

---

## Combining RBAC and ABAC

Relations and attributes compose freely. A common pattern is gating a role-based permission with an additional attribute check.

```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity repository {
        relation owner      @user;
        relation maintainer @user;
        attribute archived bool;

        // push is blocked for everyone when the repo is archived
        permission push := (owner or maintainer) and not(archived);
    }
""");
```

**Write:**
```csharp
await writer.Write(
    [new RelationTuple("repository", "repo-1", "owner", "user", "alice")],
    [new AttributeTuple("repository", "repo-1", "archived", JsonValue.Create(true))],
    ct);
```

**Check:**
```csharp
// false — repo is archived, push is blocked even for the owner
await checkEngine.Check(new CheckRequest("repository", "repo-1", "push", "user", "alice"), ct);
```

---

## Multi-Tenancy

Valtuutus does not have a built-in tenant column. The two supported approaches are described below.

### Option 1: Tenant-per-database (strongest isolation)

Give each tenant its own database. At request time, resolve the correct connection string for the tenant and pass it via `IDbDataWriterProvider` / `IDbDataReaderProvider`:

```csharp
// Register with a tenant-aware connection factory
builder.Services.AddValtuutusCore(/* schema */)
    .AddPostgres(sp =>
    {
        var tenantResolver = sp.GetRequiredService<ITenantResolver>();
        return () =>
        {
            var connectionString = tenantResolver.GetConnectionString();
            return new NpgsqlConnection(connectionString);
        };
    });
```

`ITenantResolver` is your own service — it could read the tenant from the current HTTP context, a claim, or a scoped dependency.

Each tenant's data is fully isolated; there is no risk of cross-tenant data leakage at the database level.

### Option 2: Tenant-per-schema (Postgres only)

Postgres supports multiple schemas within a single database. Valtuutus lets you configure the schema name at DI registration time. By combining this with a schema-per-tenant convention and setting `search_path` on the connection, you get isolation with shared infrastructure:

```csharp
builder.Services.AddValtuutusCore(/* schema */)
    .AddPostgres(sp =>
    {
        var tenantResolver = sp.GetRequiredService<ITenantResolver>();
        return () =>
        {
            var conn = new NpgsqlConnection(sharedConnectionString);
            // Direct Postgres to the tenant's schema
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{tenantResolver.GetSchemaName()}\"";
            cmd.ExecuteNonQuery();
            return conn;
        };
    });
```

Run the Valtuutus migration once per tenant schema when a new tenant is provisioned.

### What is not currently supported

Shared tables with a `tenant_id` column are not supported in the current data model. If you need this pattern, please open an issue — it would require a schema change to `relation_tuples` and `attributes`, and updated queries across all providers.

---

## Ownership transfer

When ownership of a resource changes, delete the old owner tuple and write the new one in a single operation:

```csharp
SnapToken token = await writer.Delete(
    new DeleteFilter
    {
        Relations = [new DeleteRelationsFilter
        {
            EntityType = "document",
            EntityId   = "doc-1",
            Relation   = "owner",
            SubjectType = "user",
            SubjectId   = "alice"
        }]
    }, ct);

token = await writer.Write(
    [new RelationTuple("document", "doc-1", "owner", "user", "bob")],
    [], ct);
```

Pass the returned `token` on subsequent checks to guarantee read-after-write consistency.
