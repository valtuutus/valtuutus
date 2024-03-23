# Storing Data

Valtuutus stores the data in a database of your preference, which serves as the single source of truth for all authorization queries and requests via the available engines.

In Valtuutus, you can store authorization data in two different forms: as relationships and as attributes.

Let’s examine relationships first.

## Relationships
In Valtuutus, relationship between your entities and objects builds up a collection of access control lists (ACLs).

These ACLs called relational tuples: the underlying data form that represents object-to-object and object-to-subject relations. 
Each relational tuple represents an action that a specific entity can do on a resource and takes form of entity U has relation R to object O, where entity U could be a simple user or a user set such as team X members.

## Attributes
Besides creating and storing your authorization-related data as relationships, you can also create attributes along with your resources and users.

For certain use cases, using relationships (ReBAC) or roles (RBAC) might not be the best fit. For example, geo-based permissions where access is granted only if associated with a geographical or regional attribute. Or consider time-based permissions, restricting certain actions to office hours. A simpler scenario involves defining certain individuals as banned, filtering them out from access despite meeting other requirements.

Attribute-Based Access Control takes a more contextual approach, allowing you to define access rights based on the context around subjects and objects in an application.


**Having said that, as of now, Valtuutus only supports boolean attributes.**

## Creating Authorization Data
Relationships and attributes can be created simply by calling the `DataEngine` function `Write`.
You can send multiple relations and attributes in a single call.

Each relational tuple or attribute should be created according to the schema you defined using the FluentApi.

Let’s follow a simple document management system example with the following Valtuutus Schema to see how to create relation tuples.

```csharp
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("user")
        .WithEntity("organization")
            .WithRelation("admin", rc => rc.WithEntityType("user"))
            .WithRelation("member", rc => rc.WithEntityType("user"))
        .WithEntity("document")
            .WithRelation("owner", rc => rc.WithEntityType("user"))
            .WithRelation("parent", rc => rc.WithEntityType("organization"))
            .WithRelation("maintainer", rc => rc.WithEntityType("user").WithEntityType("organization", "member"))
            .WithPermission("view", PermissionNode.Union("owner", "parent.member", "maintainer", "parent.admin"))
            .WithPermission("edit", PermissionNode.Union("owner", "maintainer", "parent.admin"))
            .WithPermission("delete", PermissionNode.Union("owner", "parent.admin"));
});
```
According to the schema above; when a user creates a document in an organization, more specifically let’s say, when user:1 create a document:2 we need to create the following relational tuple:
- document:2#owner@user:1

To create this relational tuple, you can call the `Write` function of the `DataEngine` as follows:

```csharp
await writer.Write([new RelationTuple("document", "2", "owner", "user", "1")], [], default);
```

### Snap Tokens
In Valtuutus, each modification to the authorization data returns a snap token.
```json
{
  "snapToken": "VrP43"
}
```
This token consists of an encoded timestamp, which in later versions will be used to get fresh results in access control queries.

