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

After a bulk purge in Postgres, run `VACUUM ANALYZE` on both tables to update statistics and reclaim pages.

How often to purge depends on your write volume. For high-throughput applications (frequent permission changes), consider scheduling a periodic purge job. For low-write applications, manual purges as needed are fine.

### Schema constants
We understand that passing around arbitrary strings can lead to errors. We developed our source generator, that reads the schema and generates
constants for Entity names, relations, permissions and attributes.

On the consuming project, add the source generator from nuget:
```shell
dotnet add package Valtuutus.Lang.SourceGen
```

Then add your schema file with the `.vtt` extension to your `.csproj` as both an `EmbeddedResource` and an `AdditionalFiles` entry — both are required for the generator to run:

```xml
<ItemGroup>
  <EmbeddedResource Include="schema.vtt" />
  <AdditionalFiles Include="schema.vtt" />
</ItemGroup>
```

The source generator picks it up at build time and generates a `SchemaConstsGen` class in the `Valtuutus.Lang` namespace. For example, given this schema:

```
entity user {}
entity document {
    relation owner @user;
    attribute public bool;
    permission view := owner or public;
    permission edit := owner;
}
```

The generator produces:

```csharp
namespace Valtuutus.Lang;

public static class SchemaConstsGen
{
    public static class User
    {
        public const string Name = "user";
    }

    public static class Document
    {
        public const string Name = "document";

        public static class Attributes
        {
            public const string Public = "public";
        }

        public static class Relations
        {
            public const string Owner = "owner";
        }

        public static class Permissions
        {
            public const string View = "view";
            public const string Edit = "edit";
        }
    }
}
```

Use these constants instead of raw strings throughout your application:

```csharp
await writer.Write(
    [new RelationTuple(
        SchemaConstsGen.Document.Name,
        "2",
        SchemaConstsGen.Document.Relations.Owner,
        SchemaConstsGen.User.Name,
        "1")],
    [],
    cancellationToken);

bool canView = await checkEngine.Check(
    new CheckRequest(
        SchemaConstsGen.Document.Name,
        "2",
        SchemaConstsGen.Document.Permissions.View,
        SchemaConstsGen.User.Name,
        "1"),
    cancellationToken);
```

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

