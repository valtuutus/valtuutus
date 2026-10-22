using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Data.MySql.Tests
{
    /// <summary>
    /// `a0 or a1 or a2` (three sibling bool attributes on the same entity) is fused by
    /// RelationalPlanRewriter into one HasAnyOfAttributesOp, answered by one statement over the
    /// attribute list. These facts run that statement against seeded rows.
    /// </summary>
    public abstract class HasAnyOfAttributesRealDbSpecsBase(MySqlFixtureBase fixture) : IAsyncLifetime
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

        public Task InitializeAsync() => fixture.ResetDatabaseAsync();

        public Task DisposeAsync() => Task.CompletedTask;

        private async Task<bool> CheckCanEdit(params string[] trueAttributes)
        {
            var services = new ServiceCollection().AddValtuutusCore(Schema);
            services.AddMySql(_ => fixture.DbFactory, fixture.Options).AddConcurrentQueryLimit(3);
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
            return (await engine.Check(new CheckRequest("doc", "d1", "can_edit", "user", "alice"), default)).Allowed;
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
}

namespace Valtuutus.Data.MySql.Tests.MySql
{
    [Collection("MySqlSpec")]
    public sealed class HasAnyOfAttributesRealDbSpecs(MySqlFixture fixture) : HasAnyOfAttributesRealDbSpecsBase(fixture);
}

namespace Valtuutus.Data.MySql.Tests.MariaDb
{
    [Collection("MariaDbSpec")]
    public sealed class HasAnyOfAttributesRealDbSpecs(MariaDbFixture fixture) : HasAnyOfAttributesRealDbSpecsBase(fixture);
}
