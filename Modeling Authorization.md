```markdown
# Modeling Authorization

Valtuutus has an API that you can model your authorization logic with. The API allows you to define arbitrary relations between users and objects, such as owner, editor, commenter, or roles like admin, manager, member. You can define your entities, relations between them, and access control decisions using a Fluent API. It includes set-algebraic operators such as intersection and union for specifying potentially complex access control policies in terms of those user-object relations.

## Developing a schema

This guide will show how to develop a Valtuutus Schema from scratch with a simple example, yet it will show almost every aspect of our modeling capabilities.

We’ll follow a simplified version of the GitHub access control system, where teams and organizations have control over the viewing, editing, or deleting access rights of repositories. First, let's see the full implementation of a simple GitHub access control example using Valtuutus Schema:

```csharp
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("user")
        .WithEntity("organization")
            .WithRelation("admin", rc => rc.WithEntityType("user"))
            .WithRelation("member", rc => rc.WithEntityType("user"))
            .WithPermission("create_repository", PermissionNode.Union("admin", "member"))
        .WithEntity("team")
            .WithRelation("parent", rc => rc.WithEntityType("organization"))
            .WithRelation("member", rc => rc.WithEntityType("user"))
            .WithPermission("edit", PermissionNode.Union("member", "parent.admin"))
        .WithEntity("repository")
            .WithRelation("parent", rc => rc.WithEntityType("organization"))
            .WithRelation("owner", rc => rc.WithEntityType("user"))
            .WithRelation("maintainer", rc => rc.WithEntityType("user")
                .WithEntityType("team", "member"))
            .WithPermission("push", PermissionNode.Union("owner", "maintainer"))
            .WithPermission("read", PermissionNode.Intersect("org.admin", PermissionNode.Union("owner", "maintainer", "org.member")))
            .WithPermission("delete", PermissionNode.Union("parent.admin", "owner"));
});
```

## Defining entities
The very first step to build Valtuutus Schema is creating your Entities. An Entity is an object that defines your resources that hold a role in your permission system.

Think of entities as tables in your database. We strongly advise naming entities the same as your database table name that it corresponds to. In that way, you can easily model and reason your authorization as well as eliminate the possibility of errors.

You can create entities using the `WithEntity` method. Let’s create some entities according to our example GitHub authorization logic.

```csharp
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("user")
        .WithEntity("organization")
        .WithEntity("team")
        .WithEntity("repository");
});
```

Entities have three different options: relations, permissions (that can also represent actions like read, write, delete, etc.), and attributes.

## Defining Relations
Relations represent relationships between entities. It’s probably the most critical part of the schema because Valtuutus is mostly based on relations between resources and their permissions.

The `WithRelation` is used to create an entity relation with name and type attributes.

**Relation Attributes**:

- name: relation name.
- type: relation type, basically the entity it’s related to (e.g., user, organization, document, etc.)

### Roles and User Types

For the sake of simplicity, let’s define only 2 user types in an organization, these are administrators and direct members of the organization.

```csharp
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("user")
        .WithEntity("organization")
            .WithRelation("admin", rc => rc.WithEntityType("user"))
            .WithRelation("member", rc => rc.WithEntityType("user"));
});
```

### Parent-Child Relationship
Let’s say teams can belong to organizations and can have a member inside of it as follows:
```csharp
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("organization")
            .WithRelation("admin", rc => rc.WithEntityType("user"))
            .WithRelation("member", rc => rc.WithEntityType("user"))
        .WithEntity("team")
            .WithRelation("parent", rc => rc.WithEntityType("organization"))
            .WithRelation("member", rc => rc.WithEntityType("user"));
});
```
The parent relation indicates the organization the team belongs to. This way, we can achieve a parent-child relationship within these entities.

### Ownership
In the GitHub workflow, organizations and users can have multiple repositories, so each repository is related to an organization and with users. We can define repository relations as follows:
```csharp
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("repository")
            .WithRelation("parent", rc => rc.WithEntityType("organization"))
            .WithRelation("owner", rc => rc.WithEntityType("user"))
            .WithRelation("maintainer", rc => rc.WithEntityType("user")
                .WithEntityType("team", "member"));
});
```

The owner relation indicates the creator of the repository, that way we can achieve ownership in Valtuutus.

### Multiple Relation Types
As you can see we have new syntax above,
```csharp
...
        .WithRelation("maintainer", rc => rc.WithEntityType("user")
        .WithEntityType("team", "member"));
```
When we look at the maintainer relation, it indicates that the maintainer can be a user as well as this user can be a team member.

## Defining Permissions
Permissions define who can perform a specific action on a resource in which circumstances.

The basic form of authorization check in Valtuutus is `Can the entity U perform action X on a resource Y?`.

### Intersection and Union
The Valtuutus Schema supports Intersect and Union operators. These operators are used to combine multiple permissions, relations, and attributes into a single permission.

#### Union
You can define permissions as union operations that return true if at least one relation or attribute check evaluates to true. Here is a simple demonstration on how to achieve permission union in our Fluent API,
```csharp
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("organization")
        ...
            .WithPermission("create_repository", PermissionNode.Union("admin", "member"));
});
```

#### Intersection
Let's get back to our GitHub example and create a read action on the repository entity to represent the usage of the Intersect operator:
```csharp
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("repository")
            ...
            .WithPermission("read", PermissionNode.Intersect("org.admin", PermissionNode.Union("owner", "maintainer", "org.member")));
});
```
You can see that you can compose complex permissions by using these two operators to achieve your desired access control logic.

## Attribute Based Permissions (ABAC)
To support Attribute Based Access Control (ABAC) in Valtuutus, you can define attributes for entities and use them in your permissions.

### Defining Attributes
Attributes are used to define properties for entities. As of now, Valtuutus only allows boolean, string, integer, and decimal attributes.

Boolean attributes can be used directly as a `PermissionNode.Leaf`, but the remaining types require the use of an attribute expression.
See the examples below:

```csharp
 // Boolean attribute
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("repository")
            ...
            .WithAttribute("public", typeof(bool))
            .WithPermission("read", PermissionNode.Intersect(
                "org.admin", PermissionNode.Union("owner", "maintainer", "org.member", "public")
                )
            );
});
```

```csharp
 // String attribute
builder.Services.AddValtuutusCore(c =>
{
    c
        .WithEntity("repository")
            ...
            .WithAttribute("status", typeof(string))
            .WithPermission("write", PermissionNode.Intersect(
                "org.admin", PermissionNode.Union(PermissionNode.Leaf("owner"), PermissionNode.Leaf("maintainer"),
                PermissionNode.Leaf("org.member"), PermissionNode.AttributeStringExpression("status", s => s == "active"))
                )
            );
});
```

```
⛔ If you don’t create the related attribute data, Valtuutus accounts the attributes checks as FALSE
```
```