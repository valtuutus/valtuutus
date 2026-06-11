using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Pools;
using Valtuutus.Data;

namespace Valtuutus.Data.InMemory.Tests;

/// <summary>
/// Regression coverage for LookupSubject entity scoping. A cascading hop (e.g. parent.is_member)
/// can dead-end and forward an EMPTY entity-id list to the next recursion level. The in-memory
/// provider treats an empty id set as "match nothing", but the SQL builders dropped the entity_id
/// predicate when the list was empty — returning every subject system-wide. The fix short-circuits
/// empty entity sets in the engine and hardens the SQL builders to match nothing.
/// </summary>
public sealed class LookupSubjectEntityScopeRepro
{
    private const string Schema = """
        entity user {}
        entity organisation {
            relation parent @organisation;
            relation member @user;
            permission is_member := member or parent.is_member;
            permission direct_member := member;
        }
        """;

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddValtuutusCore(Schema)
            .AddInMemory()
            .Services
            .BuildServiceProvider();

    private static async Task<IServiceScope> Seed(ServiceProvider sp, RelationTuple[] tuples)
    {
        var scope = sp.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await writer.Write(tuples, [], default);
        return scope;
    }

    [Fact]
    public async Task DirectMember_IsEntityScoped()
    {
        using var sp = BuildProvider();
        using var scope = await Seed(sp,
        [
            new RelationTuple("organisation", "1", "member", "user", "alice"),
            new RelationTuple("organisation", "1", "member", "user", "bob"),
            new RelationTuple("organisation", "2", "member", "user", "carol"),
        ]);
        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

        var org1 = await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "1"), default);
        var org2 = await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "2"), default);
        var org999 = await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "999"), default);

        org1.Should().BeEquivalentTo("alice", "bob");
        org2.Should().BeEquivalentTo("carol");
        org999.Should().BeEmpty();
    }

    [Fact]
    public async Task CascadingMember_ResolvesParentChain_StaysScoped()
    {
        using var sp = BuildProvider();
        // org2 has org1 as parent. alice is a direct member of org1.
        using var scope = await Seed(sp,
        [
            new RelationTuple("organisation", "2", "parent", "organisation", "1"),
            new RelationTuple("organisation", "1", "member", "user", "alice"),
            new RelationTuple("organisation", "3", "member", "user", "carol"),
        ]);
        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

        // is_member cascades through parent.is_member -> alice, but stays scoped (no carol from org3).
        (await engine.Lookup(new LookupSubjectRequest("organisation", "is_member", "user", "2"), default))
            .Should().BeEquivalentTo("alice");

        // direct_member must NOT cross the parent edge.
        (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "2"), default))
            .Should().BeEmpty();

        // A leaf org with no parent: is_member resolves only its own members (empty-hop must not leak).
        (await engine.Lookup(new LookupSubjectRequest("organisation", "is_member", "user", "1"), default))
            .Should().BeEquivalentTo("alice");
    }

    [Fact]
    public async Task SoftDelete_StaysScoped_AndDeduplicated()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();

        await writer.Write(
        [
            new RelationTuple("organisation", "1", "member", "user", "alice"),
            new RelationTuple("organisation", "2", "member", "user", "carol"),
        ], [], default);

        // Add then soft-delete dave on org1 multiple times, leaving one active row.
        await writer.Write([new RelationTuple("organisation", "1", "member", "user", "dave")], [], default);
        await writer.Delete(new DeleteFilter
        {
            Relations = [new DeleteRelationsFilter { EntityType = "organisation", EntityId = "1", Relation = "member", SubjectType = "user", SubjectId = "dave" }]
        }, default);
        await writer.Write([new RelationTuple("organisation", "1", "member", "user", "dave")], [], default);
        await writer.Delete(new DeleteFilter
        {
            Relations = [new DeleteRelationsFilter { EntityType = "organisation", EntityId = "1", Relation = "member", SubjectType = "user", SubjectId = "dave" }]
        }, default);
        await writer.Write([new RelationTuple("organisation", "1", "member", "user", "dave")], [], default);

        var result = await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "1"), default);

        // alice (active) + dave (one active row among several deleted), still scoped to org1 (no carol).
        result.Should().BeEquivalentTo("alice", "dave");
        result.Should().NotContain("carol");
    }

    [Fact]
    public async Task LookupSubject_And_Check_Agree()
    {
        using var sp = BuildProvider();
        using var scope = await Seed(sp,
        [
            new RelationTuple("organisation", "2", "parent", "organisation", "1"),
            new RelationTuple("organisation", "1", "member", "user", "alice"),
            new RelationTuple("organisation", "1", "member", "user", "bob"),
            new RelationTuple("organisation", "2", "member", "user", "carol"),
            new RelationTuple("organisation", "3", "member", "user", "dave"),
        ]);
        var lookup = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();
        var check = scope.ServiceProvider.GetRequiredService<ICheckEngine>();

        string[] subjects = ["alice", "bob", "carol", "dave"];
        string[] orgs = ["1", "2", "3"];

        foreach (var permission in new[] { "direct_member", "is_member" })
        foreach (var org in orgs)
        {
            var set = await lookup.Lookup(new LookupSubjectRequest("organisation", permission, "user", org), default);
            foreach (var subject in subjects)
            {
                var inLookup = set.Contains(subject);
                var checkResult = await check.Check(
                    new CheckRequest("organisation", org, permission, "user", subject), default);
                inLookup.Should().Be(checkResult,
                    $"LookupSubject and Check must agree for permission={permission} org={org} subject={subject}");
            }
        }
    }

    /// <summary>
    /// Provider-independent root-cause guard. Asserts the engine never issues a relation read with an
    /// empty entity-id list when a cascading hop (organisation.parent) dead-ends.
    /// </summary>
    [Fact]
    public async Task CascadingHop_NeverForwardsEmptyEntityIdList()
    {
        var reader = Substitute.For<IDataReaderProvider>();
        reader.GetLatestSnapToken(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SnapToken?>(SnapToken.MinValue));

        var emptyForwarded = false;

        reader.GetRelationsWithEntityIds(
                Arg.Any<EntityRelationFilter>(),
                Arg.Any<string>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var filter = ci.Arg<EntityRelationFilter>();
                var entityIds = ci.Arg<IEnumerable<string>>().ToList();

                if (entityIds.Count == 0)
                    emptyForwarded = true;

                var pooled = PooledList<RelationTuple>.Rent();

                // organisation:5 has a direct member (alice) but NO parent tuple.
                if (filter.Relation == "member" && entityIds.Contains("5"))
                    pooled.Add(new RelationTuple("organisation", "5", "member", "user", "alice"));

                // 'parent' lookups return nothing -> engine builds an empty id list for the next hop.
                return Task.FromResult(pooled);
            });

        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddScoped(_ => reader);
        var engine = services.BuildServiceProvider().GetRequiredService<ILookupSubjectEngine>();

        var result = await engine.Lookup(
            new LookupSubjectRequest("organisation", "is_member", "user", "5") { SnapToken = SnapToken.MinValue },
            default);

        result.Should().BeEquivalentTo("alice");
        emptyForwarded.Should().BeFalse(
            "an empty entity-id list makes the SQL builders drop the entity_id filter and return every subject system-wide");
    }
}
