using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Pools;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

/// <summary>
/// Which reader calls <see cref="LookupSubjectEngine"/> makes: entity ids forwarded into recursive
/// queries are deduplicated, a terminal userset is one joined query, a non-terminal one forwards
/// its intermediate ids. Provider-agnostic, so InMemory is enough.
/// </summary>
public sealed class LookupSubjectEngineQuerySpecs : IAsyncLifetime
{
    // Scopes and providers created by the helpers live until the test ends.
    private readonly List<IAsyncDisposable> _ownedResources = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        for (var i = _ownedResources.Count - 1; i >= 0; i--)
        {
            await _ownedResources[i].DisposeAsync();
        }
    }

    private async ValueTask<(ILookupSubjectEngine Engine, List<List<string>> Captures)> CreateEngineWithCapture(
        RelationTuple[] tuples, AttributeTuple[] attributes, string? schema = null)
    {
        var captures = new List<List<string>>();
        var services = new ServiceCollection().AddValtuutusCore(schema ?? TestsConsts.DefaultSchema);
        services.AddInMemory();
        DecorateDataReaderToCapture(services, captures);
        var serviceProvider = services.BuildServiceProvider();
        _ownedResources.Add(serviceProvider);
        var scope = serviceProvider.CreateAsyncScope();
        _ownedResources.Add(scope);
        await scope.ServiceProvider.GetRequiredService<IDataWriterProvider>().Write(tuples, attributes, default);
        return (scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>(), captures);
    }

    private static void DecorateDataReaderToCapture(IServiceCollection services, List<List<string>> captures)
    {
        // Wrap whatever provider registered IDataReaderProvider rather than mock the interface, so
        // resolution behaviour stays real and only the forwarded entity-id lists are observed.
        var original = services.Last(d => d.ServiceType == typeof(IDataReaderProvider));
        services.Remove(original);
        services.Add(ServiceDescriptor.Describe(
            typeof(IDataReaderProvider),
            sp => new CapturingReader(CreateInner(sp, original), captures),
            original.Lifetime));
    }

    private static IDataReaderProvider CreateInner(IServiceProvider sp, ServiceDescriptor original)
    {
        if (original.ImplementationInstance is IDataReaderProvider instance)
            return instance;
        if (original.ImplementationFactory is not null)
            return (IDataReaderProvider)original.ImplementationFactory(sp)!;
        return (IDataReaderProvider)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
    }

    // Two-hop tuple-to-userset chain (workspace.view := groups.members → group.members := teams.member)
    // so a duplicate intermediate entity appears at an *intermediate* forwarding step — exactly where
    // the id-list bloat lived.
    private const string DedupSchema = """
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
    public async Task Lookup_DoesNotForwardDuplicateEntityIds_ToRecursiveLookups()
    {
        // Arrange — capture every entity-id list the engine forwards into a recursive query.
        var (engine, captures) = await CreateEngineWithCapture(ConvergingTuples, [], DedupSchema);

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

        // Specifically, the group -> team -> member leg (a terminal sub-relation, so it's
        // collapsed into one joined query) is fed the two distinct starting ids [g1, g2] — the
        // t1 convergence they both map to is deduplicated inside the join itself and never
        // materializes as a separate forwarded id list.
        captures.Should().Contain(ids => ids.Count == 2 && ids.Contains("g1") && ids.Contains("g2"));
    }

    [Fact]
    public async Task LookupSubject_TerminalSubRelation_UsesJoinedQuery_NotEntityIdForwarding()
    {
        // project.lead: @team#lead — team.lead is a terminal direct relation (@user only, no
        // further sub-relation paths), so this leg qualifies for the joined fast path and must
        // never forward an intermediate entity-id list into a separate recursive query.
        var (engine, captures) = await CreateEngineWithCapture(
            [
                new RelationTuple("project", "p1", "lead", "team", "t1", "lead"),
                new RelationTuple("team", "t1", "lead", "user", "alice"),
            ], []);

        var result = await engine.Lookup(
            new LookupSubjectRequest("project", "lead", "user", "p1"),
            CancellationToken.None);

        result.Should().BeEquivalentTo("alice");
        // A single capture for the joined query itself (fed the starting id [p1]) — and no
        // second capture for an intermediate id like "t1", which is what the old two-query
        // dependent-then-recurse fallback would have produced.
        captures.Should().ContainSingle().Which.Should().BeEquivalentTo("p1");
    }

    [Fact]
    public async Task LookupSubject_NonTerminalSubRelation_FallsBackToEntityIdForwarding()
    {
        // project.member: @user @team#member — team.member is NOT terminal here, since it also
        // has a @group#member sub-path (per DefaultSchema), so this leg must keep using the
        // dependent-query-then-recurse fallback rather than the joined fast path.
        var (engine, captures) = await CreateEngineWithCapture(
            [
                new RelationTuple("project", "p1", "member", "team", "t1", "member"),
                new RelationTuple("team", "t1", "member", "user", "alice"),
            ], []);

        var result = await engine.Lookup(
            new LookupSubjectRequest("project", "member", "user", "p1"),
            CancellationToken.None);

        result.Should().BeEquivalentTo("alice");
        captures.Should().Contain(ids => ids.Count == 1 && ids[0] == "t1");
    }

    /// <summary>
    /// Thin decorator that delegates every call to the real provider and records the entity-id
    /// lists passed into <see cref="GetRelationsWithEntityIds"/> and <see cref="GetRelationsJoinedByEntityIds"/> —
    /// the two entry points through which a recursion level forwards ids into the next query.
    /// </summary>
    private sealed class CapturingReader(IDataReaderProvider inner, List<List<string>> captures) : IDataReaderProvider
    {
        public Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter,
            string subjectType, IEnumerable<string> entityIds, string subjectRelation, CancellationToken cancellationToken)
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
            string[] subjectsIds, string subjectType, string subjectRelation, EntityScope? scope, CancellationToken cancellationToken)
            => inner.GetRelationsWithSubjectsIds(entityFilter, subjectsIds, subjectType, subjectRelation, scope, cancellationToken);

        public Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIdsMultiRelation(string entityType,
            string[] relationNames, string[] subjectsIds, string subjectType, string subjectRelation, SnapToken snapToken, EntityScope? scope,
            CancellationToken cancellationToken)
            => inner.GetRelationsWithSubjectsIdsMultiRelation(entityType, relationNames, subjectsIds, subjectType, subjectRelation, snapToken, scope, cancellationToken);

        public Task<PooledList<RelationTuple>> GetRelationsWithEntityIdsMultiRelation(string entityType,
            string[] relationNames, string subjectType, IEnumerable<string> entityIds, string subjectRelation,
            SnapToken snapToken, CancellationToken cancellationToken)
        {
            var materialized = entityIds.ToList();
            captures.Add(materialized);
            return inner.GetRelationsWithEntityIdsMultiRelation(entityType, relationNames, subjectType, materialized,
                subjectRelation, snapToken, cancellationToken);
        }

        public Task<PooledList<RelationTuple>> GetRelationsJoined(EntityRelationFilter mainFilter, string subEntityType,
            string subjectRelation, string subRelation, string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
            => inner.GetRelationsJoined(mainFilter, subEntityType, subjectRelation, subRelation, subjectType, subjectId, scope, cancellationToken);

        public Task<PooledList<RelationTuple>> GetRelationsJoinedByEntityIds(EntityRelationFilter mainFilter,
            IEnumerable<string> entityIds, string subEntityType, string subjectRelation, string subRelation,
            string subjectType, CancellationToken cancellationToken)
        {
            var materialized = entityIds.ToList();
            captures.Add(materialized);
            return inner.GetRelationsJoinedByEntityIds(mainFilter, materialized, subEntityType, subjectRelation, subRelation, subjectType, cancellationToken);
        }

        public Task<bool> HasTrueBoolAttribute(string entityType, string entityId, string attribute,
            SnapToken snapToken, CancellationToken cancellationToken)
            => inner.HasTrueBoolAttribute(entityType, entityId, attribute, snapToken, cancellationToken);

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
