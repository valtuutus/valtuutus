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
Relationships and attributes can be created simply by calling the `IDataWriterProvider` function `Write`.
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

### Schema constants
We understand that passing around arbitrary strings can lead to errors. We developed our source generator, that reads the schema and generates
constants for Entity names, relations, permissions and attributes.

On the consuming project, add the source generator from nuget:
```shell
dotnet add package Valtuutus.Lang.SourceGen
```

Then you should add your schema as an embedded file ending with .vtt;
The source generator will pick it up and generate the consts in the Valtuutus.Lang namespace.

### Snap Tokens
In Valtuutus, each modification to the authorization data returns a snap token.
```json
{
  "snapToken": "01J59G4294E1AR1AMCJTD0SPXW"
}
```
This token consists of an [ULID](https://github.com/ulid/spec) that can be used to get fresh or older results in access control queries.

