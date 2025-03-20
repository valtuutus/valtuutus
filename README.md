# Valtuutus

## A Google Zanzibar inspired authorization library in .NET

The implementation is inspired on [permify](https://github.com/Permify/permify) and other ReBac open source projects.


[![NuGet Version](https://img.shields.io/nuget/vpre/Valtuutus.Core?logo=nuget)](https://www.nuget.org/packages?q=Valtuutus&includeComputedFrameworks=true&prerel=true&sortby=relevance)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=coverage)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Technical Debt](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=sqale_index)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Code Smells](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=code_smells)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Reliability Rating](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=reliability_rating)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Vulnerabilities](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=vulnerabilities)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Bugs](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=bugs)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=security_rating)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Maintainability Rating](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=sqale_rating)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)

<a href="https://bencher.dev/perf/valtuutus?key=true&reports_per_page=4&branches_per_page=8&testbeds_per_page=8&benchmarks_per_page=8&plots_per_page=8&reports_page=1&branches_page=1&testbeds_page=1&benchmarks_page=1&plots_page=1&report=f7c35e30-f513-402c-af44-56d8c876fff3&branches=9e4cbdcf-9fee-4cd3-ada1-62aefe433145&heads=5bdd1841-0a0f-4532-84b7-87ef3d065302&testbeds=072da3db-e609-4676-99a6-5b9262df6086&benchmarks=22f44ad6-7979-4757-aa00-3286de603788%2Cd0d39808-ab7a-42f9-95fb-9193853640f3%2Cd3f76a50-964b-4526-9c33-89a38c18f474%2Cb0c2d4d2-2cae-4dfd-89d2-35b49a00b23e&measures=b549a9dd-6ff0-4525-b90a-c9e3af815580&start_time=1714521600000&lower_boundary=false&upper_boundary=false&clear=true&lower_value=false&upper_value=false&x_axis=date_time&end_time=1796083200000&utm_medium=share&utm_source=bencher&utm_content=img&utm_campaign=perf%2Bimg&utm_term=valtuutus"><img src="https://api.bencher.dev/v0/projects/valtuutus/perf/img?branches=9e4cbdcf-9fee-4cd3-ada1-62aefe433145&heads=5bdd1841-0a0f-4532-84b7-87ef3d065302&testbeds=072da3db-e609-4676-99a6-5b9262df6086&benchmarks=22f44ad6-7979-4757-aa00-3286de603788%2Cd0d39808-ab7a-42f9-95fb-9193853640f3%2Cd3f76a50-964b-4526-9c33-89a38c18f474%2Cb0c2d4d2-2cae-4dfd-89d2-35b49a00b23e&measures=b549a9dd-6ff0-4525-b90a-c9e3af815580&start_time=1714521600000&end_time=1797083200000" title="valtuutus" alt="valtuutus - Bencher" /></a>

## Functionality
The library is designed to be simple and easy to use. Each subset of functionality is divided in engines. The engines are:
- [ICheckEngine](src/Valtuutus.Core/Engines/Check/ICheckEngine.cs): The engine that handles the answering of two questions:
  - `Can entity U perform action Y in resource Z`? For that, use the `Check` function.
  - `What permissions entity U have in resource Z`? For that, use the `SubjectPermission` function.
- [ILookupSubjectEngine](src/Valtuutus.Core/Engines/LookupSubject/ILookupSubjectEngine.cs): The engine that can answer: `Which subjects of type T have permission Y on entity:X?` For that, use the `Lookup` function.
- [ILookupEntityEngine](src/Valtuutus.Core/Engines/LookupEntity/ILookupEntityEngine.cs): The engine that can answer: `Which resources of type T can entity:X have permission Y?` For that, use the `LookupEntity` function.
- [IDataWriterProvider](src/Valtuutus.Core/Data/IDataWriterProvider.cs): This is the provider that can write your relational or attribute data.
- [IDbDataWriterProvider](src/Valtuutus.Data.Db/IDbDataWriterProvider.cs): Works similarly to `IDataWriterProvider`, with the addition of accepting a connection and transaction as parameters.
- [Read here](Storing%20Data.md) about how the relational data is stored.

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
Our database providers allows the customization of schema and table names to your needs. When adding to dependency injection, checkout the optional parameter.

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
