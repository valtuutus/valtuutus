# Source Generator

`Valtuutus.Lang.SourceGen` reads your schema at build time and generates strongly-typed C# constants and compiled functions for it, so you don't have to pass around raw strings.

Install it in the consuming project:
```shell
dotnet add package Valtuutus.Lang.SourceGen
```

Then add your schema file with the `.vtt` extension to your `.csproj` as an `EmbeddedResource`:

```xml
<ItemGroup>
  <EmbeddedResource Include="schema.vtt" />
</ItemGroup>
```

## Schema constants

We understand that passing around arbitrary strings can lead to errors. We developed our source generator, that reads the schema and generates
constants for Entity names, relations, permissions and attributes.

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

## Schema functions

If your schema defines custom `fn` blocks (see [Schema Reference](Schema%20Reference.md)), the same source generator can also compile them at build time instead of leaving them to be interpreted at schema-load time.

By default, a schema's `fn` bodies are compiled into a `System.Linq.Expressions` tree the first time the schema is parsed (`ParameterIdFnNode.GetExpression` in `src/Valtuutus.Core/Lang/ExpressionNode.cs`). This works fine under the normal JIT, but it relies on reflection that **throws at runtime under Native AOT**, and even outside AOT it's extra interpreter/allocation overhead paid on every call.

`Valtuutus.Lang.SourceGen` avoids this by emitting `SchemaFunctionsGen.All` — a build-time-compiled `IReadOnlyDictionary<string, Func<IDictionary<string, object?>, bool>>` covering every `fn` in the schema. Pass it into `AddValtuutusCore` as the second argument:

```csharp
builder.Services.AddValtuutusCore(schemaText, SchemaFunctionsGen.All);
```

The `Stream` overload of `AddValtuutusCore` takes the same second argument.

**This is opt-in and silent if omitted** — there's no compile-time or publish-time warning, only a runtime `ArgumentException` under Native AOT (or the extra Expression-tree overhead otherwise). If your schema uses custom `fn`, always pass `SchemaFunctionsGen.All` once you've added the source generator package. Function names not present in the map (e.g. schemas loaded from a source not known at compile time, such as a database or admin API) still fall back to the Expression-tree path automatically.
