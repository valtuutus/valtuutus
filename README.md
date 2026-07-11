# Valtuutus

Valtuutus is a high-performance **Relationship-Based Access Control (ReBAC)** authorization library for **.NET**, inspired by **Google Zanzibar**.

It enables **fine-grained authorization** for **ASP.NET Core** and .NET applications — modeling permissions and access control through relationships instead of relying only on roles.

Features:
- Google Zanzibar-inspired permission model
- High-performance authorization engine
- ASP.NET Core / .NET integration
- Strongly typed schema support (source generator)
- Multiple evaluation engines (Check, LookupEntity, LookupSubject, SubjectPermission, Explain)
- Low-allocation execution
- Native AOT compatible

The implementation is inspired on [permify](https://github.com/Permify/permify) and other ReBAC open source projects.


[![NuGet Version](https://img.shields.io/nuget/vpre/Valtuutus.Core?logo=nuget)](https://www.nuget.org/packages?q=Valtuutus&includeComputedFrameworks=true&prerel=true&sortby=relevance)

<a href="https://bencher.dev/perf/valtuutus?clear=true&key=true&reports_per_page=4&branches_per_page=8&testbeds_per_page=8&benchmarks_per_page=8&plots_per_page=8&reports_page=1&branches_page=2&testbeds_page=1&benchmarks_page=1&plots_page=1&tab=benchmarks&measures=b549a9dd-6ff0-4525-b90a-c9e3af815580&branches_search=dev&branches=1bffa1be-8399-4560-814d-30231501957f&heads=deb5918c-87b4-4bdb-9b46-25e563bdba14&testbeds=072da3db-e609-4676-99a6-5b9262df6086&benchmarks_search=check_complex&benchmarks=86537617-e761-40b8-bd7a-8ad4b6559b47%2C177b6be7-d449-45cb-928a-04f792c80c43%2C8dc1fb40-6499-45e0-be05-23bbf539cd7e%2Cfa90791e-1700-480d-a973-a5df9b695431&start_time=1782864000000&utm_medium=share&utm_source=bencher&utm_content=img&utm_campaign=perf%2Bimg&utm_term=valtuutus"><img src="https://api.bencher.dev/v0/projects/valtuutus/perf/img?branches=1bffa1be-8399-4560-814d-30231501957f&heads=deb5918c-87b4-4bdb-9b46-25e563bdba14&testbeds=072da3db-e609-4676-99a6-5b9262df6086&specs=&benchmarks=86537617-e761-40b8-bd7a-8ad4b6559b47%2C177b6be7-d449-45cb-928a-04f792c80c43%2C8dc1fb40-6499-45e0-be05-23bbf539cd7e%2Cfa90791e-1700-480d-a973-a5df9b695431&measures=b549a9dd-6ff0-4525-b90a-c9e3af815580&start_time=1782864000000" title="valtuutus" alt="valtuutus - Bencher" /></a>

## Functionality
The library is designed to be simple and easy to use. Each subset of functionality is divided in engines. The engines are:
- [ICheckEngine](src/Valtuutus.Core/Engines/Check/ICheckEngine.cs): The engine that handles the answering of three questions:
  - `Can entity U perform action Y in resource Z`? For that, use the `Check` function.
  - `What permissions entity U have in resource Z`? For that, use the `SubjectPermission` function.
  - `Why did that permission check succeed or fail?` For that, use the `Explain` function — returns a full resolution tree showing every evaluated relation, attribute, and expression.
- [ILookupSubjectEngine](src/Valtuutus.Core/Engines/LookupSubject/ILookupSubjectEngine.cs): The engine that can answer: `Which subjects of type T have permission Y on entity:X?` For that, use the `Lookup` function.
- [ILookupEntityEngine](src/Valtuutus.Core/Engines/LookupEntity/ILookupEntityEngine.cs): The engine that can answer: `Which resources of type T can entity:X have permission Y?` For that, use the `LookupEntity` function. Supports **scoped queries** and **cursor pagination** — see below.
- [IDataWriterProvider](src/Valtuutus.Core/Data/IDataWriterProvider.cs): This is the provider that can write your relational or attribute data.
- [IDbDataWriterProvider](src/Valtuutus.Data.Db/IDbDataWriterProvider.cs): Works similarly to `IDataWriterProvider`, with the addition of accepting a connection and transaction as parameters.
- [Read here](Storing%20Data.md) about how the relational data is stored.
- [Read here](Using%20the%20Engines.md) for engine usage examples (Check, Explain, SubjectPermission, LookupSubject, LookupEntity).

## LookupEntity — scoped queries and pagination

`LookupEntity` returns a `LookupEntityPage`:

```csharp
LookupEntityPage page = await lookupEntityEngine.LookupEntity(
    new LookupEntityRequest("task", "view", "user", "alice"),
    cancellationToken);

// page.EntityIds — IReadOnlyList<string>
// page.ContinuationToken — null if no more pages
```

### Scope — constrain results to a parent entity

Use `EntityScope` when you need to answer a scoped question like
**"which tasks in project X can this user view?"** — the same query you'd back a
`GET /projects/{projectId}/tasks` endpoint with.

Without scope, `LookupEntity` returns all tasks the user can view across the entire system.
With scope, results are limited to tasks that have the specified relation to the given parent entity —
so only tasks belonging to `project-1` are considered.

```csharp
var page = await lookupEntityEngine.LookupEntity(
    new LookupEntityRequest("task", "view", "user", "alice")
    {
        Scope = new EntityScope(
            Relation: "parent",      // the relation on "task" that points to its parent
            SubjectType: "project",  // the parent entity type
            SubjectId: "project-1"   // the specific parent to scope to
        )
    },
    cancellationToken);
```

### Pagination

```csharp
string? token = null;
do
{
    var page = await lookupEntityEngine.LookupEntity(
        new LookupEntityRequest("task", "view", "user", "alice")
        {
            Scope = new EntityScope("parent", "project", "project-1"),
            PageSize = 50,
            ContinuationToken = token
        },
        cancellationToken);

    Process(page.EntityIds);
    token = page.ContinuationToken;
} while (token is not null);
```

## Documentation

| Guide | Description |
|---|---|
| [Getting Started](Getting%20Started.md) | End-to-end quickstart — install, configure, write data, check permissions |
| [Modeling Authorization](Modeling%20Authorization.md) | Schema DSL walkthrough with the GitHub example |
| [Schema Reference](Schema%20Reference.md) | Complete reference for every keyword, operator, and type in the DSL |
| [Authorization Patterns](Authorization%20Patterns.md) | Ready-made patterns: RBAC, hierarchical RBAC, ABAC, multi-tenancy |
| [Using the Engines](Using%20the%20Engines.md) | Code examples for Check, Explain, SubjectPermission, LookupSubject, LookupEntity, depth |
| [Storing Data](Storing%20Data.md) | Writing, deleting, snap tokens |
| [Source Generator](Source%20Generator.md) | Build-time schema constants and compiled `fn` functions |
| [Testing](Testing.md) | Unit-testing your authorization model with the InMemory provider |
| [Caching](Caching.md) | Reducing database load with FusionCache |
| [Telemetry](Telemetry.md) | OpenTelemetry activity sources, emitted spans, and what to monitor |

## Usage
Install the package from NuGet:

### If using Postgres:
```shell
dotnet add package Valtuutus.Data.Postgres
```

### If using SqlServer:
```shell
dotnet add package Valtuutus.Data.SqlServer
```

### If you prefer using an InMemory provider:
```shell
dotnet add package Valtuutus.Data.InMemory
```

## Adding to DI:
```csharp
builder.Services.AddValtuutusCore(c =>
        ... 
```
See examples of how to define your schema [here](Modeling%20Authorization.md).

### If using Postgres:
```csharp
builder.Services
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!));
```

### If using SqlServer:
```csharp
builder.Services
    .AddSqlServer(_ => () => new SqlConnection(builder.Configuration.GetConnectionString("SqlServerDb")!));
```

### If using InMemory:
```csharp
builder.Services
    .AddInMemory();
```

## Database migrations
If you are using a DB provider to store your data, please look at the scripts that create the tables that Valtuutus require to function.
- [Postgres](src/Valtuutus.Data.Postgres/Database/migrations/20240221201712_initial.sql)
- [SqlServer](src/Valtuutus.Data.SqlServer/Database/migrations/20240224120604_initial.sql)

## Schema and table name customization

Both relational providers accept an optional options object to customise the database schema and table names. Pass it as the second argument to `AddPostgres` or `AddSqlServer`:

```csharp
// Postgres — defaults: schema="public", tables="transactions", "relation_tuples", "attributes"
builder.Services.AddValtuutusCore(/* schema */)
    .AddPostgres(
        _ => () => new NpgsqlConnection(connectionString),
        new ValtuutusPostgresOptions(
            schema:                 "authz",
            transactionsTableName:  "transactions",
            relationsTableName:     "relation_tuples",
            attributesTableName:    "attributes"));

// SQL Server — defaults: schema="dbo", same table names
builder.Services.AddValtuutusCore(/* schema */)
    .AddSqlServer(
        _ => () => new SqlConnection(connectionString),
        new ValtuutusSqlServerOptions(
            schema:                 "authz",
            transactionsTableName:  "transactions",
            relationsTableName:     "relation_tuples",
            attributesTableName:    "attributes"));
```

Make sure the migration script targets the same schema and table names you configure here.

`ValtuutusPostgresOptions` also exposes two Npgsql-specific properties for automatic prepared statements:

| Property | Default | Meaning |
|---|---|---|
| `MaxAutoPrepare` | `64` | Maximum number of statements Npgsql will auto-prepare |
| `AutoPrepareMinUsages` | `2` | Minimum executions before a statement is prepared |

These map directly to [Npgsql's prepared statement feature](https://www.npgsql.org/doc/prepare.html) and can improve performance for repeated queries under load.

## Using query concurrent limiting
It is expected that you don't want to allow Valtuutus to expand queries while it has resources. The default limit is 5 concurrent queries for the same request. To change that, you can use the `AddConcurrentQueryLimit` method, for example:
```csharp
builder.Services
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!)) // Replace this with any provider you want
    .AddConcurrentQueryLimit(10);
```
Change your data provider according to your database.

## Caching
Valtuutus supports caching the calls to the engines through the Valtuutus.Data.Caching package.
To use it, install like:
```shell
dotnet add package Valtuutus.Data.Caching
```
In your DI setup, add the caching component:
```csharp
builder.Services
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!)) // Replace this with any provider you want
    .AddCaching(); // <-- This line
```

This packages requires that you set up the amazing [FusionCache library](https://github.com/ZiggyCreatures/FusionCache).
[Click here](Caching.md) for more information.

## Telemetry
The library uses [OpenTelemetry](https://opentelemetry.io/) to provide telemetry data. To enable it, just add a source with the name "Valtuutus":
```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(telemetry =>
    {
        telemetry
            .AddSource("Valtuutus")
            ...
```
