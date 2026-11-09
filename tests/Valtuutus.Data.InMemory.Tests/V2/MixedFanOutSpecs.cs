using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Data.InMemory.Tests.V2;

/// <summary>
/// A tuple-to-userset whose tupleset holds subjects of several types answers each type's subjects
/// with one batched lookup when the computed relation is a plain direct relation there.
/// </summary>
public sealed class MixedFanOutSpecs
{
    // holder takes a userset target, so the arrow is read through the tupleset at run time. The arrow
    // evaluates owner on the userset's own entity (team).
    private const string Schema = """
        entity user {}
        entity service_account {
            relation owner @user;
        }
        entity team {
            relation owner @user;
            relation member @user;
        }
        entity doc {
            relation holder @service_account @team#member;
            permission edit := holder.owner;
        }
        """;

    [Theory]
    [InlineData("caio", true)]
    [InlineData("bia", true)]
    [InlineData("nobody", false)]
    public async Task Each_subject_type_of_the_tupleset_is_answered_by_one_batch(string user, bool expected)
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema).AddInMemory();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDataWriterProvider>().Write([
            new RelationTuple("doc", "1", "holder", "service_account", "deployer"),
            new RelationTuple("doc", "1", "holder", "service_account", "builder"),
            new RelationTuple("doc", "1", "holder", "team", "ops", "member"),
            new RelationTuple("doc", "1", "holder", "team", "dev", "member"),
            new RelationTuple("service_account", "builder", "owner", "user", "caio"),
            new RelationTuple("team", "dev", "owner", "user", "bia"),
        ], [], default);

        var explained = await provider.GetRequiredService<ICheckEngine>().Explain(
            new CheckRequest("doc", "1", "edit", "user", user), default);

        explained.Result.Should().Be(expected);
        Details(explained.Root).Should().Contain(d => d.StartsWith("batch:"));
        Children(explained.Root).Should().NotContain(n => n.EntityType == "service_account" || n.EntityType == "team",
            "the subjects are answered in batches, not one by one");
    }

    private static IEnumerable<string> Details(CheckNode node) =>
        (node.Detail is { } d ? [d] : Enumerable.Empty<string>()).Concat(node.Children.SelectMany(Details));

    private static IEnumerable<CheckNode> Children(CheckNode node) =>
        node.Children.Concat(node.Children.SelectMany(Children));
}
