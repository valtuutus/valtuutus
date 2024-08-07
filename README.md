# Valtuutus

## A Google Zanzibar inspired authorization library in .NET

The implementation is based on the [permify](https://github.com/Permify/permify) and other ReBac open source projects.


![NuGet Version](https://img.shields.io/nuget/vpre/Valtuutus.Core?logo=nuget)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=coverage)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Technical Debt](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=sqale_index)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)

[![Code Smells](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=code_smells)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Reliability Rating](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=reliability_rating)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Vulnerabilities](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=vulnerabilities)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Bugs](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=bugs)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)

[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=security_rating)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)
[![Maintainability Rating](https://sonarcloud.io/api/project_badges/measure?project=valtuutus_valtuutus&metric=sqale_rating)](https://sonarcloud.io/summary/new_code?id=valtuutus_valtuutus)

## Functionality
The library is designed to be simple and easy to use. Each subset of functionality is divided in engines. The engines are:
- [DataEngine](src/Valtuutus.Core/DataEngine.cs): The engine that handles the write and deletion of relation tuples and attributes.
  - [Read here](Storing%20Data.md) about how the relational data is stored.
- [CheckEngine](src/Valtuutus.Core/CheckEngine.cs): The engine that handles the answering of two questions:
  - `Can entity U perform action Y in resource Z`? For that, use the `Check` function.
  - `What permissions entity U have in resource Z`? For that, use the `SubjectPermission` function.
- [LookupSubjectEngine](src/Valtuutus.Core/LookupSubjectEngine.cs): The engine that can answer: `Which subjects of type T have permission Y on entity:X?` For that, use the `Lookup` function.
- [LookupEntityEngine](src/Valtuutus.Core/LookupEntityEngine.cs): The engine that can answer: `Which resources of type T can entity:X have permission Y?` For that, use the `LookupEntity` function.

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
builder.Services.
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!));
```

### If using SqlServer:
```csharp
builder.Services.
    .AddSqlServer(_ => () => new SqlConnection(builder.Configuration.GetConnectionString("SqlServerDb")!));
```

### If using InMemory:
```csharp
builder.Services.
    .AddInMemory();
```

### Using check concurrent limiting
It is expected that you don't want to allow Valtuutus to expand queries while it has resources. The default limit is 5 concurrent queries for the same request. To change that, you can use the AddConcurrentQueryLimit method, for example:
```csharp
builder.Services.
    .AddPostgres(_ => () => new NpgsqlConnection(builder.Configuration.GetConnectionString("PostgresDb")!)) // Replace this with any provider you want
    .AddConcurrentQueryLimit(10);
```
Change your data provider according to your database.

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
