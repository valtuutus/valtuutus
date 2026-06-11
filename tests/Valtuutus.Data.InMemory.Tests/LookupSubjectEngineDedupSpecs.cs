using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Pools;

namespace Valtuutus.Data.InMemory.Tests;

/// <summary>
/// Engine-level unit tests (mocked <see cref="IDataReaderProvider"/>) verifying that the
/// entity-id list the engine feeds into the next recursion level is deduplicated.
///
/// Regression: a single subject commonly appears across many tuples, so the list built from
/// one level's results (and used purely as an `entity_id IN (...)` filter set at the next level)
/// accumulated duplicates — bloating the SQL Server TVP / Postgres id array and the round-trip.
/// </summary>
public sealed class LookupSubjectEngineDedupSpecs
{
    private const string Schema = """
        entity user {}
        entity group {
            relation member @user;
        }
        entity workspace {
            relation group_members @group;
            permission view := group_members.member;
        }
        """;

    [Fact]
    public async Task EntityIds_ForwardedToNextLevel_AreDeduplicated()
    {
        // Arrange
        var reader = Substitute.For<IDataReaderProvider>();
        reader.GetLatestSnapToken(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SnapToken?>(SnapToken.MinValue));

        // Captures the entity-id list passed into the second (group -> member) query, which is
        // exactly the list produced by ToSubjectIdList from the first level's tuples.
        List<string>? memberQueryEntityIds = null;

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

                if (filter.Relation == "group_members")
                {
                    // Three intermediate tuples that all resolve to the SAME group (g1).
                    // Without dedup, ToSubjectIdList would yield [g1, g1, g1].
                    var pooled = PooledList<RelationTuple>.Rent();
                    pooled.Add(new RelationTuple("workspace", "w1", "group_members", "group", "g1"));
                    pooled.Add(new RelationTuple("workspace", "w1", "group_members", "group", "g1"));
                    pooled.Add(new RelationTuple("workspace", "w1", "group_members", "group", "g1"));
                    return Task.FromResult(pooled);
                }

                // Second level: group -> member. Record what the engine forwarded.
                memberQueryEntityIds = entityIds;
                var members = PooledList<RelationTuple>.Rent();
                members.Add(new RelationTuple("group", "g1", "member", "user", "alice"));
                return Task.FromResult(members);
            });

        var engine = BuildEngine(reader);

        // Act
        var result = await engine.Lookup(
            new LookupSubjectRequest("workspace", "view", "user", "w1") { SnapToken = SnapToken.MinValue },
            CancellationToken.None);

        // Assert
        result.Should().BeEquivalentTo("alice");

        memberQueryEntityIds.Should().NotBeNull("the group -> member query must have been issued");
        memberQueryEntityIds!.Should().OnlyHaveUniqueItems("duplicate ids bloat the TVP / id array without changing results");
        memberQueryEntityIds.Should().BeEquivalentTo("g1");
    }

    private static ILookupSubjectEngine BuildEngine(IDataReaderProvider reader)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddScoped(_ => reader);
        return services.BuildServiceProvider().GetRequiredService<ILookupSubjectEngine>();
    }
}
