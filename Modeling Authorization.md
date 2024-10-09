# Modeling Authorization

Valtuutus has an API that you can model your authorization logic with. The API allows you to define arbitrary relations between users and objects, such as owner, editor, commenter, or roles like admin, manager, member. You can define your entities, relations between them, and access control decisions using our custom dsl. It includes set-algebraic operators such as intersection and union for specifying potentially complex access control policies in terms of those user-object relations.

## Ways of providing a schema
The method .AddValtuutusCore have two overloads, one that accepts a string and other that accepts a stream.
You can pass your embedded file or file stream to it, and it will read the schema all the same.

## Developing a schema

This guide will show how to develop a Valtuutus Schema from scratch with a simple example, yet it will show almost every aspect of our modeling capabilities.

We’ll follow a simplified version of the GitHub access control system, where teams and organizations have control over the viewing, editing, or deleting access rights of repositories. First, let's see the full implementation of a simple GitHub access control example using Valtuutus Schema:
```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity organization {
        relation admin @user;
        relation member @user;
        permission create_repository := admin or member;
    }
    entity team {
        relation parent @organization;
        relation member @user;
        permission edit := member or parent.admin;      
    }
    entity repository {
        attribute public bool;
        attribute archived bool;
        relation parent @organization;
        relation owner @user;
        relation maintainer @user @team#member;
        permission push := owner or maintainer and notArchived(archived);
        permission read := public and (org.admin or owner or maintainer or org.member);
        permission delete := parent.admin or owner;
   }
   
   fn notArchived(archived bool) => not(archived); 
""");
```


## Defining entities
The very first step to build Valtuutus Schema is creating your Entities. An Entity is an object that defines your resources that hold a role in your permission system.
Think of entities as tables in your database. You can create entities using the entity declaration.
Let’s create some entities according to our example GitHub authorization logic.


```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity organization {}
    entity team {}
    entity repository {}
""");
```

Entities have three different options: relations, permissions (that can also represent actions like read, write, delete, etc.), and attributes.

## Defining Relations
Relations represent relationships between entities. It’s probably the most critical part of the schema because Valtuutus is mostly based on relations between resources and their permissions.

The relation declaration is used to create an entity relation with name and type attributes.

**Relation Attributes**:

- name: relation name.
- type: relation type, basically the entity it’s related to (e.g., user, organization, document, etc.)

### Roles and User Types

For the sake of simplicity, let’s define only 2 user types in an organization, these are administrators and direct members of the organization.

```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity organization {
        relation admin @user;
        relation member @user;
    }
""");
```

### Parent-Child Relationship
Let’s say teams can belong to organizations and can have a member inside of it as follows:
```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity organization {
        relation admin @user;
        relation member @user;
    }
    entity team {
        relation parent @organization;
        relation member @user;
    }
""");
```

The parent relation indicates the organization the team belongs to. This way, we can achieve a parent-child relationship within these entities.

### Ownership
In the GitHub workflow, organizations and users can have multiple repositories, so each repository is related to an organization and with users. We can define repository relations as follows:
```csharp
builder.Services.AddValtuutusCore("""
    entity user {}
    entity organization {...}
    entity repository {
        relation parent @organization;
        relation owner @user;
        relation maintainer @user @team#member;
""");
```

The owner relation indicates the creator of the repository, that way we can achieve ownership in Valtuutus.

### Multiple Relation Types
As you can see we have a new syntax above,
```csharp
"""
...
        relation maintainer @user @team#member;
"""
```
When we look at the maintainer relation, it indicates that the maintainer can be a user as well as this user can be a team member.

## Defining Permissions
Permissions define who can perform a specific action on a resource in which circumstances.

The basic form of authorization check in Valtuutus is `Can the entity U perform action X on a resource Y?`.

### Intersection and Union
The Valtuutus Schema supports Intersect and Union operators. These operators are used to combine multiple permissions, relations, and attributes into a single permission.

#### Union
You can define permissions as union operations that return true if at least one relation or attribute check evaluates to true. Here is a simple demonstration on how to achieve permission union in our dsl:
```csharp
builder.Services.AddValtuutusCore("""
    ...
    entity organization {
        ...
        permission create_repository := admin or member;
    }
""");
```

#### Intersection
Let's get back to our GitHub example and create a read action on the repository entity to represent the usage of the Intersect operator:
```csharp
builder.Services.AddValtuutusCore("""
    ...
    entity repository {
        ...
        permission read := org.admin and (owner or mantainer or org.member);
    }
""");
```

You can see that you can compose complex permissions by using these two operators to achieve your desired access control logic.

## Attribute Based Permissions (ABAC)
To support Attribute Based Access Control (ABAC) in Valtuutus, you can define attributes for entities and use them in your permissions.

### Defining Attributes
Attributes are used to define properties for entities. As of now, Valtuutus only allows boolean, string, integer, and decimal attributes.

Boolean attributes can be used directly in the body of a permission, but the remaining types require the use of a function.
See the examples below:

```csharp
 // Boolean attribute
builder.Services.AddValtuutusCore("""
    ...
    entity repository {
        ...
        attribute public bool;
        permission read := org.admin and (owner or mantainer or org.member or public);
    }
""");
```

```csharp
 // String attribute
builder.Services.AddValtuutusCore("""
    ...
    entity repository {
        ...
        attribute status string;
        permission write := org.admin and (owner or mantainer or org.member) and isActiveStatus(status);
   
    }
    fn isActiveStatus(status string) => status == "active";
""");
```

```
⛔ If you don’t create the related attribute data, Valtuutus accounts the attributes checks as FALSE
```
## Functions
Functions, in Valtuutus, are a way to implement custom rules that are not supported by the traditional permission expression.
They are declared at the same level as entities in the schema.
Let's go back to our previous example:
```csharp
builder.Services.AddValtuutusCore("""
    ...
    fn isActiveStatus(status string) => status == "active";
""");
```
This declares a function that accepts a single parameter of type string, and checks if it is equal to the literal "active".

Functions accept the same types as attributes: bool, string, decimal and int;

They have more flexibility when it comes to operators, let's go through them:
- `==` -> Equals;
- `!=` -> Not equal; 
- `>` -> Greater; ⛔ Does not work with bool.
- `>=` -> Greater or equal; ⛔ Does not work with bool;
- `<` -> Less; ⛔ Does not work with bool;
- `<=` -> Less or equal; ⛔ Does not work with bool;
- `or` -> Or; Both sides must evaluate to a boolean expression.
- `and` -> And; Both sides must evaluate to a boolean expression.
- `not()` -> Not; The value passed inside must evaluate to a boolean expression.

With these operators, you can mix and match to create a rule that fits your needs.

Calling a function inside a permission is simple:

```csharp
"""
        permission push := owner or maintainer and notArchived(archived);
        ...
"""
```
Functions can receive the number of parameters you want, currently there's no limit to it. There is no way to define a optional parameter.
During a function call, you can use a special accessor, called `context`.
You can get data from the context to use as a parameter to a function. For example:

Let's say you only know during the request that the repository is archived or not.
```csharp
    permission push := owner or maintainer and notArchived(context.archived);
```
You can pass this data in any of the request using the following property:
```csharp
    public required IDictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
```
Just set whatever data you want, following our example:
```csharp
    var context = new Dictionary<string, object>();
    context["archived"] = false;
    new CheckRequest(..., context);
```
Please be sure that you are giving the correct type to the dictionary, or runtime errors can occur!
