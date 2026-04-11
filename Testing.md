# Testing Your Authorization Model

Valtuutus provides an InMemory provider that is ideal for unit and integration testing. It requires no Docker, no database, and resets between tests automatically by simply creating a new `ServiceProvider`.

## Setup

Install the InMemory package in your test project:

```shell
dotnet add package Valtuutus.Data.InMemory
```

## Basic test structure

Each test creates a fresh `ServiceProvider`, writes the tuples and attributes it needs, and then calls the engine:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Xunit;

public class DocumentPermissionTests
{
    private static ServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddValtuutusCore("""
                entity user {}
                entity document {
                    relation owner  @user;
                    relation viewer @user;
                    permission view := owner or viewer;
                    permission edit := owner;
                }
            """)
            .AddInMemory()
            .Services
            .BuildServiceProvider();

    [Fact]
    public async Task Owner_CanEditDocument()
    {
        await using var sp = BuildServices();
        var writer = sp.GetRequiredService<IDataWriterProvider>();
        var engine = sp.GetRequiredService<ICheckEngine>();

        await writer.Write(
            [new RelationTuple("document", "doc-1", "owner", "user", "alice")],
            [],
            CancellationToken.None);

        var result = await engine.Check(
            new CheckRequest("document", "doc-1", "edit", "user", "alice"),
            CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task Viewer_CannotEditDocument()
    {
        await using var sp = BuildServices();
        var writer = sp.GetRequiredService<IDataWriterProvider>();
        var engine = sp.GetRequiredService<ICheckEngine>();

        await writer.Write(
            [new RelationTuple("document", "doc-1", "viewer", "user", "bob")],
            [],
            CancellationToken.None);

        var result = await engine.Check(
            new CheckRequest("document", "doc-1", "edit", "user", "bob"),
            CancellationToken.None);

        Assert.False(result);
    }
}
```

Because a new `ServiceProvider` is created per test (or per test class), state never leaks between tests.

## Testing attributes

```csharp
[Fact]
public async Task PublicDocument_IsViewableByAnyone()
{
    await using var sp = new ServiceCollection()
        .AddValtuutusCore("""
            entity user {}
            entity document {
                attribute public bool;
                relation owner @user;
                permission view := owner or public;
            }
        """)
        .AddInMemory()
        .Services
        .BuildServiceProvider();

    var writer = sp.GetRequiredService<IDataWriterProvider>();
    var engine = sp.GetRequiredService<ICheckEngine>();

    // Write the attribute — no owner tuple
    await writer.Write(
        [],
        [new AttributeTuple("document", "doc-1", "public", JsonValue.Create(true))],
        CancellationToken.None);

    var result = await engine.Check(
        new CheckRequest("document", "doc-1", "view", "user", "anyone"),
        CancellationToken.None);

    Assert.True(result);
}
```

## Testing with context

```csharp
[Fact]
public async Task Push_IsBlocked_WhenContextSaysArchived()
{
    await using var sp = new ServiceCollection()
        .AddValtuutusCore("""
            entity user {}
            entity repository {
                relation owner @user;
                permission push := owner and notArchived(context.archived);
            }
            fn notArchived(archived bool) => not(archived);
        """)
        .AddInMemory()
        .Services
        .BuildServiceProvider();

    var writer = sp.GetRequiredService<IDataWriterProvider>();
    var engine = sp.GetRequiredService<ICheckEngine>();

    await writer.Write(
        [new RelationTuple("repository", "repo-1", "owner", "user", "alice")],
        [],
        CancellationToken.None);

    var result = await engine.Check(
        new CheckRequest(
            "repository", "repo-1", "push", "user", "alice",
            context: new Dictionary<string, object> { ["archived"] = true }),
        CancellationToken.None);

    Assert.False(result);
}
```

## Testing LookupSubject

```csharp
[Fact]
public async Task LookupSubject_ReturnsAllViewers()
{
    await using var sp = new ServiceCollection()
        .AddValtuutusCore("""
            entity user {}
            entity document {
                relation viewer @user;
                permission view := viewer;
            }
        """)
        .AddInMemory()
        .Services
        .BuildServiceProvider();

    var writer = sp.GetRequiredService<IDataWriterProvider>();
    var lookupEngine = sp.GetRequiredService<ILookupSubjectEngine>();

    await writer.Write([
        new RelationTuple("document", "doc-1", "viewer", "user", "alice"),
        new RelationTuple("document", "doc-1", "viewer", "user", "bob"),
    ], [], CancellationToken.None);

    var result = await lookupEngine.Lookup(
        new LookupSubjectRequest("document", "view", "user", "doc-1"),
        CancellationToken.None);

    Assert.Equal(new HashSet<string> { "alice", "bob" }, result);
}
```

## Sharing a schema across a test class

If many tests share the same schema but need fresh data, use a factory method per test while building the provider only once for the schema string:

```csharp
public class CheckEngineTests
{
    private const string Schema = """
        entity user {}
        entity document {
            relation owner @user;
            permission view := owner;
        }
    """;

    private async Task<(ICheckEngine engine, IDataWriterProvider writer, AsyncServiceScope scope)>
        CreateAsync()
    {
        var sp = new ServiceCollection()
            .AddValtuutusCore(Schema)
            .AddInMemory()
            .Services
            .BuildServiceProvider();

        var scope = sp.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
        return (engine, writer, scope);
    }

    [Fact]
    public async Task Test1()
    {
        var (engine, writer, scope) = await CreateAsync();
        await using (scope)
        {
            await writer.Write([new RelationTuple("document", "1", "owner", "user", "alice")], [], default);
            Assert.True(await engine.Check(new CheckRequest("document", "1", "view", "user", "alice"), default));
        }
    }
}
```

## Tips

- The InMemory provider is completely in-process — no network, no disk, no cleanup needed.
- Each `ServiceProvider` instance is a fully independent data store. Creating a new one is the reset mechanism.
- Use parameterized tests (`[Theory]` + `[MemberData]`) to cover many tuple/check combinations without boilerplate.
- If you want to test snap token behavior, capture the `SnapToken` returned by `Write` and pass it to the engine request.
