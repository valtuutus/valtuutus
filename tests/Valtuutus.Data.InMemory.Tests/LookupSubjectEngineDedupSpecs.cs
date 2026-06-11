using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Pools;

namespace Valtuutus.Data.InMemory.Tests;

/// <summary>
/// Verifies that the entity-id list the engine feeds into the next recursion level is
/// deduplicated, using the real <see cref="AddInMemory"/> provider seeded with data that
/// genuinely converges.
///
/// Regression: a single intermediate entity commonly appears across many tuples (here two
/// groups both belong to the same team), so the list built from one level's results — used
/// purely as an <c>entity_id IN (...)</c> filter set at the next level — accumulated duplicates,
/// bloating the SQL Server TVP / Postgres id array and the round-trip without changing the result.
///
/// The schema below is a two-hop tuple-to-userset chain
/// (<c>workspace.view := groups.members</c> → <c>group.members := teams.member</c>) so that the
/// duplicate appears at an *intermediate* forwarding step, which is exactly where the bloat lived.
/// </summary>
public sealed class LookupSubjectEngineDedupSpecs
{
    private const string Schema = """
        entity user {}
        entity team {
            relation member @user;
        }
        entity group {
            relation teams @team;
            permission members := teams.member;
        }
        entity workspace {
            relation groups @group;
            permission view := groups.members;
        }
        """;

    // g1 and g2 both belong to t1, so resolving `teams` for [g1, g2] yields the team id twice.
    private static readonly RelationTuple[] ConvergingTuples =
    [
        new("workspace", "w1", "groups", "group", "g1"),
        new("workspace", "w1", "groups", "group", "g2"),
        new("group", "g1", "teams", "team", "t1"),
        new("group", "g2", "teams", "team", "t1"),
        new("team", "t1", "member", "user", "alice"),
    ];

    [Fact]
    public async Task Lookup_WithConvergingIntermediateEntities_ReturnsDistinctSubjects()
    {
        // Arrange
        var (engine, _) = await BuildEngine(capture: false);

        // Act
        var result = await engine.Lookup(
            new LookupSubjectRequest("workspace", "view", "user", "w1"),
            CancellationToken.None);

        // Assert — alice is reachable through both g1 and g2, but must appear exactly once.
        result.Should().BeEquivalentTo("alice");
    }

    [Fact]
    public async Task Lookup_DoesNotForwardDuplicateEntityIds_ToRecursiveLookups()
    {
        // Arrange — capture every entity-id list the engine forwards into a recursive query.
        var (engine, captures) = await BuildEngine(capture: true);

        // Act
        var result = await engine.Lookup(
            new LookupSubjectRequest("workspace", "view", "user", "w1"),
            CancellationToken.None);

        // Assert
        result.Should().BeEquivalentTo("alice");

        // Performance invariant: no recursion level is ever handed a list containing duplicates,
        // even though `teams` for [g1, g2] resolves to t1 twice.
        captures.Should().NotBeEmpty("the engine must have issued at least one entity-id query");
        captures.Should().OnlyContain(
            ids => ids.Distinct().Count() == ids.Count,
            "duplicate ids bloat the TVP / id array without changing results");

        // Specifically, the team -> member leaf query is fed the deduplicated [t1], not [t1, t1].
        captures.Should().Contain(ids => ids.Count == 1 && ids[0] == "t1");
    }

    /// <summary>
    /// Builds a real in-memory engine over <see cref="ConvergingTuples"/>. When
    /// <paramref name="capture"/> is set, the <see cref="IDataReaderProvider"/> is wrapped so every
    /// entity-id list forwarded into <see cref="IDataReaderProvider.GetRelationsWithEntityIds"/> is
    /// recorded — a hand-rolled fake that observes the forwarded ids without mocking behaviour.
    /// </summary>
    private static async Task<(ILookupSubjectEngine Engine, List<List<string>> Captures)> BuildEngine(bool capture)
    {
        var captures = new List<List<string>>();

        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema);
        services.AddInMemory();

        if (capture)
        {
            // Decorate the real provider rather than mock the interface, so resolution behaviour
            // stays real and only the forwarded ids are observed.
            services.AddScoped<IDataReaderProvider>(sp =>
                new CapturingReader(sp.GetRequiredService<InMemoryProvider>(), captures));
        }

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await writer.Write(ConvergingTuples, [], CancellationToken.None);

        var engine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();
        return (engine, captures);
    }

    /// <summary>
    /// Thin decorator that delegates every call to the real provider and records the entity-id
    /// lists passed into <see cref="GetRelationsWithEntityIds"/>.
    /// </summary>
    private sealed class CapturingReader(IDataReaderProvider inner, List<List<string>> captures) : IDataReaderProvider
    {
        public Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter,
            string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
        {
            var materialized = entityIds.ToList();
            captures.Add(materialized);
            return inner.GetRelationsWithEntityIds(entityRelationFilter, subjectType, materialized, subjectRelation,
                cancellationToken);
        }

        public Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
            => inner.GetLatestSnapToken(cancellationToken);

        public Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
            => inner.GetRelations(tupleFilter, cancellationToken);

        public Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
            => inner.HasDirectRelation(tupleFilter, subjectId, cancellationToken);

        public Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation, string subjectId,
            SnapToken snapToken, CancellationToken cancellationToken)
            => inner.HasAnyDirectRelation(entityType, entityIds, relation, subjectId, snapToken, cancellationToken);

        public Task<bool> HasTupleToUserSetRelation(string entityType, string entityId, string tupleSetRelation,
            string subEntityType, string computedRelation, string subjectType, string subjectId, SnapToken snapToken,
            CancellationToken cancellationToken)
            => inner.HasTupleToUserSetRelation(entityType, entityId, tupleSetRelation, subEntityType, computedRelation,
                subjectType, subjectId, snapToken, cancellationToken);

        public Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
            => inner.GetIndirectRelations(tupleFilter, cancellationToken);

        public Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter,
            string[] subjectsIds, string subjectType, EntityScope? scope, CancellationToken cancellationToken)
            => inner.GetRelationsWithSubjectsIds(entityFilter, subjectsIds, subjectType, scope, cancellationToken);

        public Task<PooledList<RelationTuple>> GetRelationsJoined(EntityRelationFilter mainFilter, string subEntityType,
            string subRelation, string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
            => inner.GetRelationsJoined(mainFilter, subEntityType, subRelation, subjectType, subjectId, scope, cancellationToken);

        public Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
            => inner.GetAttribute(filter, cancellationToken);

        public Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
            => inner.GetAttributes(filter, cancellationToken);

        public Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(
            EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
            => inner.GetAttributes(filter, scope, cancellationToken);

        public Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds,
            CancellationToken cancellationToken)
            => inner.GetAttributesWithEntityIds(filter, entitiesIds, cancellationToken);

        public Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(
            EntityAttributesFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
            => inner.GetAttributesWithEntityIds(filter, entitiesIds, cancellationToken);

        public Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
            => inner.GetAttributesSingleEntity(filter, cancellationToken);

        public Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds,
            SnapToken snapToken, CancellationToken cancellationToken)
            => inner.GetEntityIdsExcluding(entityType, excludeIds, snapToken, cancellationToken);

        public Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds,
            SnapToken snapToken, CancellationToken cancellationToken)
            => inner.GetSubjectIdsExcluding(subjectType, excludeIds, snapToken, cancellationToken);
    }
}
