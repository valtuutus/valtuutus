using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.Postgres.Tests;

/// <summary>
/// R4 real-Postgres correctness proof (DbBatch-Task 8, Step 2): `a0 or a1 or a2` (three sibling
/// bool attributes on the same entity) compiles to a single MultiAttributeNode
/// (PlanCompiler.GroupSiblingAttributeTruth) which the relational rewriter turns into one
/// HasAnyOfAttributesOp — one `attribute = ANY(@attributes)` round trip instead of three. This
/// proves that fused SQL actually matches real seeded rows correctly, not just fakes/NSubstitute
/// (which is all Tasks 1-7 exercised).
/// </summary>
[Collection("PostgreSqlSpec")]
public sealed class HasAnyOfAttributesRealDbSpecs : IAsyncLifetime
{
    private const string Schema = """
        entity user {}
        entity doc {
            attribute a0 bool;
            attribute a1 bool;
            attribute a2 bool;
            permission can_edit := a0 or a1 or a2;
        }
        """;

    public HasAnyOfAttributesRealDbSpecs(PostgresFixture fixture)
    {
        Fixture = fixture;
    }

    private PostgresFixture Fixture { get; }

    public Task InitializeAsync() => Fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<bool> CheckCanEdit(params string[] trueAttributes)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddPostgres(_ => ((IWithDbConnectionFactory)Fixture).DbFactory).AddConcurrentQueryLimit(3);
        services.AddValtuutusCheckV2();
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();

        if (trueAttributes.Length > 0)
        {
            var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
            var attributeTuples = trueAttributes
                .Select(a => new AttributeTuple("doc", "d1", a, JsonValue.Create(true)))
                .ToArray();
            await writer.Write([], attributeTuples, default);
        }

        var engine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
        return await engine.Check(new CheckRequest("doc", "d1", "can_edit", "user", "alice"), default);
    }

    [Fact]
    public async Task NoAttributeTrue_ReturnsFalse()
    {
        (await CheckCanEdit()).Should().BeFalse();
    }

    [Theory]
    [InlineData("a0")]
    [InlineData("a1")]
    [InlineData("a2")]
    public async Task ExactlyOneAttributeTrue_ReturnsTrue(string attribute)
    {
        (await CheckCanEdit(attribute)).Should().BeTrue();
    }

    [Fact]
    public async Task AllAttributesTrue_ReturnsTrue()
    {
        (await CheckCanEdit("a0", "a1", "a2")).Should().BeTrue();
    }
}
