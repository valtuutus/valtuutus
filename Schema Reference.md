# Schema Language Reference

Valtuutus uses a custom DSL to define your authorization model. This page is the complete reference for every keyword, construct, and operator the language supports.

---

## Top-level declarations

A schema is a sequence of `entity` and `fn` declarations. Order does not matter.

```
entity user {}
entity document { ... }

fn isActive(status string) => status == "active";
```

---

## `entity`

Declares a resource type that participates in the authorization model. An entity body may contain any combination of `relation`, `attribute`, and `permission` declarations.

```
entity <name> {
    <relation | attribute | permission> *
}
```

Entity names must be unique within the schema. An entity with no body (e.g. `entity user {}`) is valid and typically used to represent subjects.

---

## `relation`

Declares a named edge between this entity and one or more other entity types.

```
relation <name> @<type> [@<type> ...] [#<relation>];
```

**Single type:**
```
relation owner @user;
```

**Multiple allowed types:**
```
relation member @user @service_account;
```

**Type with relation specifier (`@type#relation`):**

Allows assigning a *user set* (all subjects that have a given relation to another entity) as the value of this relation. For example, "maintainer can be a user directly, or any member of a team":

```
relation maintainer @user @team#member;
```

When a tuple is written with a subject of `team#member`, the engine resolves that to all current members of the team at check time.

---

## `attribute`

Declares a named property of this entity. Attribute values are stored alongside the entity and used in permission expressions.

```
attribute <name> <type>;
```

Supported types:

| Type      | Example value | Notes |
|-----------|--------------|-------|
| `bool`    | `true`       | Can be used directly in a permission expression |
| `string`  | `"active"`   | Requires a `fn` for comparison |
| `int`     | `42`         | Requires a `fn` for comparison |
| `decimal` | `3.14`       | Requires a `fn` for comparison |

> If an attribute tuple has not been written for an entity, any attribute check in a permission expression evaluates to `false`.

---

## `permission`

Declares a named boolean rule that the engines evaluate.

```
permission <name> := <expression>;
```

A permission expression can be:

| Form | Meaning |
|------|---------|
| `relation_name` | Subject has this relation to the entity |
| `attribute_name` | Attribute is `true` (bool only) |
| `fn_name(arg, ...)` | Custom function call evaluates to `true` |
| `parent_relation.permission_or_relation` | Traverse a relation to another entity and check a permission/relation there |
| `a and b` | Both sides must be true (intersection) |
| `a or b` | At least one side must be true (union) |
| `not(expr)` | Negates the inner expression |

Expressions compose freely:

```
permission view := owner or (parent.admin and not(archived));
```

**Traversal with dot notation**

Use `<relation>.<permission>` to walk through a relation to another entity type and evaluate a permission there:

```
entity team {
    relation parent @organization;
    permission manage := parent.admin;   // "parent" is a relation; "admin" is a permission on organization
}
```

Multiple hops are allowed:

```
permission view := parent.parent.admin;
```

---

## `fn`

Declares a pure function that can be called from a `permission` expression. Functions may not reference relations — they operate only on attribute values and context.

```
fn <name>(<param> <type> [, ...]) => <fn_expression>;
```

```
fn isActive(status string)       => status == "active";
fn inBudget(amount decimal)      => amount <= 1000.00;
fn isAdult(age int)              => age >= 18;
fn notArchived(archived bool)    => not(archived);
```

**Supported operators in `fn` expressions:**

| Operator | Meaning | Works with |
|----------|---------|-----------|
| `==`     | Equals | all types |
| `!=`     | Not equal | all types |
| `>`      | Greater than | `int`, `decimal`, `string` |
| `>=`     | Greater or equal | `int`, `decimal`, `string` |
| `<`      | Less than | `int`, `decimal`, `string` |
| `<=`     | Less or equal | `int`, `decimal`, `string` |
| `and`    | Logical AND | both sides must be boolean |
| `or`     | Logical OR | both sides must be boolean |
| `not()`  | Logical NOT | inner must be boolean |

Functions can accept any number of parameters. Optional parameters are not supported.

---

## `context` accessor

Inside a `permission` expression or `fn` call, you can read runtime values from the request context using `context.<key>`:

```
permission push := owner or maintainer and notArchived(context.archived);
```

Supply context values on the request object:

```csharp
new CheckRequest(..., context: new Dictionary<string, object> { ["archived"] = false })
```

The runtime type of the value must match the declared `fn` parameter type (`bool`, `int`, `string`, `decimal`). A missing or null context key causes the check to evaluate as `false`.

---

## Complete example

```
entity user {}

entity organization {
    relation admin  @user;
    relation member @user;
}

entity team {
    relation parent @organization;
    relation owner  @user;
    relation member @user;

    permission manage := owner or parent.admin;
    permission view   := member or owner or parent.member or parent.admin;
}

entity document {
    relation parent     @organization;
    relation owner      @user;
    relation maintainer @user @team#member;
    attribute public    bool;
    attribute status    string;

    permission view   := owner or public or maintainer or parent.member or parent.admin;
    permission edit   := owner or maintainer or parent.admin and isActive(status);
    permission delete := owner or parent.admin;
}

fn isActive(status string) => status == "active";
```
