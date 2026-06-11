using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Schemas;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data;
using Valtuutus.Core.Data;
using Valtuutus.Core.Lang;
using Valtuutus.Core.Pools;

namespace Valtuutus.Tests.Shared;

public abstract class BaseLookupSubjectEngineSpecs : IAsyncLifetime
{
    protected BaseLookupSubjectEngineSpecs(IDatabaseFixture fixture)
    {
        Fixture = fixture;
    }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    protected IDatabaseFixture Fixture { get; }

    private ServiceProvider CreateServiceProvider(string? schema = null, List<List<string>>? captures = null)
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(schema ?? TestsConsts.DefaultSchema);
        AddSpecificProvider(services)
            .AddConcurrentQueryLimit(3);

        if (captures is not null)
        {
            DecorateDataReaderToCapture(services, captures);
        }

        return services.BuildServiceProvider();
    }


    private async ValueTask<ILookupSubjectEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        string? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var lookupSubjectEngine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();
        if(tuples.Length == 0 && attributes.Length == 0) return lookupSubjectEngine;
        var provider = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await provider.Write(tuples, attributes, default);
        return lookupSubjectEngine;
    }

    /// <summary>
    /// Builds an engine like <see cref="CreateEngine"/> but wraps the configured provider's
    /// <see cref="IDataReaderProvider"/> so every entity-id list the engine forwards into a
    /// recursive query is recorded. Resolution behaviour stays real — only the forwarded ids are
    /// observed — and the decoration is provider-agnostic, so the same invariant is exercised
    /// against the in-memory, SQL Server, and Postgres providers.
    /// </summary>
    private async ValueTask<(ILookupSubjectEngine Engine, List<List<string>> Captures)> CreateEngineWithCapture(
        RelationTuple[] tuples, AttributeTuple[] attributes, string? schema = null)
    {
        var captures = new List<List<string>>();
        var serviceProvider = CreateServiceProvider(schema, captures);
        var scope = serviceProvider.CreateScope();
        var lookupSubjectEngine = scope.ServiceProvider.GetRequiredService<ILookupSubjectEngine>();
        var provider = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await provider.Write(tuples, attributes, default);
        return (lookupSubjectEngine, captures);
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


    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        TopLevelChecks => LookupSubjectEngineSpecList.TopLevelChecks;


    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupSubjectRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }


    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        IndirectRelationLookup = LookupSubjectEngineSpecList.IndirectRelationLookup;

    [Theory]
    [MemberData(nameof(IndirectRelationLookup))]
    public async Task IndirectRelationLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        SimplePermissionLookup = LookupSubjectEngineSpecList.SimplePermissionLookup;

    [Theory]
    [MemberData(nameof(SimplePermissionLookup))]
    public async Task SimplePermissionLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        IntersectWithRelationAndAttributePermissionLookup =
            LookupSubjectEngineSpecList.IntersectWithRelationAndAttributePermissionLookup;

    [Theory]
    [MemberData(nameof(IntersectWithRelationAndAttributePermissionLookup))]
    public async Task IntersectWithRelationAndAttributeLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        IntersectAttributeExpWithOtherNodesPermissionLookup =
            LookupSubjectEngineSpecList.IntersectAttributeExpWithOtherNodes;

    [Theory]
    [MemberData(nameof(IntersectAttributeExpWithOtherNodesPermissionLookup))]
    public async Task IntersectAttributeExpWithOtherNodesLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, HashSet<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task TestStringBasedAttributeExpression()
    {
        // arrange
        var schema = @"
            entity user {}
            entity workspace {
                relation member @user;
                attribute status string;
                permission edit := isActiveStatus(status) and member;
            }
            fn isActiveStatus(status string) => status == ""active"";
        ";

        // act
        var engine = await CreateEngine(
            [
                new RelationTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new RelationTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier,
                    TestsConsts.Users.Bob),
            ],
            [
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "status",
                    JsonValue.Create("active")!),
            ], schema);

        // Act
        var result = await engine.Lookup(new LookupSubjectRequest(TestsConsts.Workspaces.Identifier,
            "edit", "user", TestsConsts.Workspaces.PublicWorkspace), default);

        // assert
        result.Should()
            .BeEquivalentTo(TestsConsts.Users.Alice, TestsConsts.Users.Bob);
    }

    [Fact]
    public async Task TestDecimalBasedAttributeExpression()
    {
        // arrange
        var schema = @"
            entity user {}
            entity account {
                relation owner @user;
                attribute balance decimal;
                permission withdraw := owner and check_balance(balance);
            }
            fn check_balance(balance decimal) => balance >= 500.0;
        ";

        // act
        var engine = await CreateEngine(
            [
                new RelationTuple("account", "1", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new RelationTuple("account", "2", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            ],
            [
                new AttributeTuple("account", "1", "balance",
                    JsonValue.Create(872.54m)),
                new AttributeTuple("account", "2", "balance",
                    JsonValue.Create(12.11m)),
            ], schema);

        // Act
        var result = await engine.Lookup(new LookupSubjectRequest("account",
            "withdraw", "user", "1"), default);

        // assert
        result.Should().BeEquivalentTo(TestsConsts.Users.Alice);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        UnionRelationDepthLimit = LookupSubjectEngineSpecList.UnionRelationDepthLimit;

    [Theory]
    [MemberData(nameof(UnionRelationDepthLimit))]
    public async Task LookupEntityWithDepthLimit(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, HashSet<string> expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity group {
                relation member @user;
            }
            entity workspace {
                relation group_members @group;
                permission view := group_members.member;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Lookup(request, default);

        // Assert
        result.Should().BeEquivalentTo(expected);
    }
        
    public static TheoryData<string, decimal?, string, HashSet<string>> ContextAccessTheoryData = new()
    {
        {"withdraw_amount", 500.0m, "1", [TestsConsts.Users.Alice]},
        {"withdraw_amount", 500.0m, "2", []},
        {"withdraw_amount", 1000.0m, "1", []},
        {"withdraw_amount", 872.54m, "1", [TestsConsts.Users.Alice]},
        {"withdraw_amount", 100.0m, "2", [TestsConsts.Users.Bob]},
        {"withdraw_amount", null, "1", []},
        {"withdraw_amount", null, "2", []},
        {"amount", 500.0m, "1", []},
        {"amount", 500.0m, "2", []}
    };

    [Theory, MemberData(nameof(ContextAccessTheoryData))]
    public async Task Test_function_call_with_context_arg(string key, decimal? value, string reqAcc, HashSet<string> expected)
    {
        // arrange
        var schema = @"
            entity user {}
            entity account {
                relation owner @user;
                attribute balance decimal;
                permission withdraw := owner and check_balance(balance, context.withdraw_amount);
            }
            fn check_balance(balance decimal, withdrawAmount decimal) => balance >= withdrawAmount;
        ";


        // act
        var engine = await CreateEngine(
            [
                new RelationTuple("account", "1", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new RelationTuple("account", "2", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            ],
            [
                new AttributeTuple("account", "1", "balance",
                    JsonValue.Create(872.54m)),
                new AttributeTuple("account", "2", "balance",
                    JsonValue.Create(120.11m)),
            ], schema);

        // Act
        var context = new Dictionary<string, object> {{key, value}};
        var result = await engine.Lookup(new LookupSubjectRequest("account",
            "withdraw", "user", reqAcc, context: context), default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task TypeGuard_ShouldReturnEmpty_WhenSubjectTypeNotAllowedInRelation()
    {
        // The 'owner' relation on 'workspace' only allows 'user', not 'group'.
        // LookupSubject for subject type 'group' should return nothing without a DB call.
        const string schema = """
            entity user {}
            entity group { relation member @user; }
            entity workspace {
                relation owner @user;
                permission delete := owner;
            }
            """;

        var engine = await CreateEngine(
            [new RelationTuple("workspace", "1", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)],
            [], schema);

        var result = await engine.Lookup(
            new LookupSubjectRequest("workspace", "delete", "group", "1"),
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task LookupSubject_CascadingHop_StaysEntityScoped()
    {
        // Regression: when a cascading hop (parent.is_member) dead-ends, the engine forwards an
        // empty entity-id list to the next level. The SQL builders used to drop the entity_id
        // predicate on an empty list and return every subject system-wide, diverging from the
        // entity-scoped Check engine. Run against every provider to catch the SQL-specific defect.
        const string schema = """
            entity user {}
            entity organisation {
                relation parent @organisation;
                relation member @user;
                permission is_member := member or parent.is_member;
                permission direct_member := member;
            }
            """;

        var engine = await CreateEngine(
        [
            new RelationTuple("organisation", "2", "parent", "organisation", "1"),
            new RelationTuple("organisation", "1", "member", "user", "alice"),
            new RelationTuple("organisation", "1", "member", "user", "bob"),
            new RelationTuple("organisation", "2", "member", "user", "carol"),
            new RelationTuple("organisation", "3", "member", "user", "dave"),
        ], [], schema);

        // direct_member is a plain alias: strictly the org's own members, never system-wide.
        (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "1"), default))
            .Should().BeEquivalentTo("alice", "bob");
        (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "2"), default))
            .Should().BeEquivalentTo("carol");
        (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "999"), default))
            .Should().BeEmpty();

        // is_member cascades through parent.is_member, but stays scoped (org2 -> org1 members only).
        (await engine.Lookup(new LookupSubjectRequest("organisation", "is_member", "user", "2"), default))
            .Should().BeEquivalentTo("alice", "bob", "carol");

        // org1 has no parent: the empty parent hop must not leak dave (org3) or anyone else.
        (await engine.Lookup(new LookupSubjectRequest("organisation", "is_member", "user", "1"), default))
            .Should().BeEquivalentTo("alice", "bob");
    }

    [Fact]
    public async Task LookupSubject_Not_Relation_Returns_Subjects_That_Are_Not_Owner()
    {
        const string schema = """
            entity user {}
            entity document {
                relation owner @user;
                relation editor @user;
                permission non_owner := not(owner);
            }
            """;

        var engine = await CreateEngine(
            [
                new RelationTuple("document", "doc1", "owner", "user", "alice"),
                new RelationTuple("document", "doc1", "editor", "user", "bob"),
            ], [], schema);

        var result = await engine.Lookup(
            new LookupSubjectRequest("document", "non_owner", "user", "doc1"),
            CancellationToken.None);

        // alice is owner → excluded; bob is editor (exists in relation_tuples) but not owner → included
        result.Should().Contain("bob");
        result.Should().NotContain("alice");
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
    public async Task Lookup_WithConvergingIntermediateEntities_ReturnsDistinctSubjects()
    {
        // Arrange
        var engine = await CreateEngine(ConvergingTuples, [], DedupSchema);

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

        // Specifically, the team -> member leaf query is fed the deduplicated [t1], not [t1, t1].
        captures.Should().Contain(ids => ids.Count == 1 && ids[0] == "t1");
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