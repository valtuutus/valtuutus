using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Schemas;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Data;
using Valtuutus.Core.Data;
using Valtuutus.Core.Lang;

namespace Valtuutus.Tests.Shared;

public abstract class BaseLookupSubjectEngineSpecs : IAsyncLifetime
{
    // Scopes and providers created by the helpers live until the test ends.
    private readonly List<IAsyncDisposable> _ownedResources = [];

    protected BaseLookupSubjectEngineSpecs(IDatabaseFixture fixture)
    {
        Fixture = fixture;
    }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    protected IDatabaseFixture Fixture { get; }

    private ServiceProvider CreateServiceProvider(string? schema = null)
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(schema ?? TestsConsts.DefaultSchema);
        AddSpecificProvider(services)
            .AddConcurrentQueryLimit(3);

        return services.BuildServiceProvider();
    }


    private async ValueTask<ILookupSubjectEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        string? schema = null)
    {
        var scope = await CreateScope(tuples, attributes, schema);
        return scope.GetRequiredService<ILookupSubjectEngine>();
    }

    /// <summary>
    /// Seeds a scope and returns its <see cref="IServiceProvider"/> so a test can resolve several
    /// services from the same scope (e.g. the writer for interleaved deletes, or both the
    /// LookupSubject and Check engines).
    /// </summary>
    private async ValueTask<IServiceProvider> CreateScope(RelationTuple[] tuples, AttributeTuple[] attributes,
        string? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        _ownedResources.Add(serviceProvider);
        var scope = serviceProvider.CreateAsyncScope();
        _ownedResources.Add(scope);
        if (tuples.Length != 0 || attributes.Length != 0)
        {
            var provider = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
            await provider.Write(tuples, attributes, default);
        }

        return scope.ServiceProvider;
    }


    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        for (var i = _ownedResources.Count - 1; i >= 0; i--)
        {
            await _ownedResources[i].DisposeAsync();
        }
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
        var result = (await engine.Lookup(request, default)).SubjectIds;

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
        var result = (await engine.Lookup(request, default)).SubjectIds;

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
        var result = (await engine.Lookup(request, default)).SubjectIds;

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
        var result = (await engine.Lookup(request, default)).SubjectIds;

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
        var result = (await engine.Lookup(request, default)).SubjectIds;

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
        var result = (await engine.Lookup(new LookupSubjectRequest(TestsConsts.Workspaces.Identifier,
            "edit", "user", TestsConsts.Workspaces.PublicWorkspace), default)).SubjectIds;

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
        var result = (await engine.Lookup(new LookupSubjectRequest("account",
            "withdraw", "user", "1"), default)).SubjectIds;

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
        var result = (await engine.Lookup(request, default)).SubjectIds;

        // Assert
        result.Should().BeEquivalentTo(expected);
    }

    // group.member mixes a nested userset with a terminal one, so a chain can end in either.
    private const string UsersetDepthSchema = """
        entity user {}
        entity team {
            relation member @user;
        }
        entity group {
            relation member @user @group#member @team#member;
        }
        entity project {
            relation reviewer @team#member;
            relation viewer @group#member;
        }
        """;

    // project -> team -> user is two hops whether or not the engine reads both in one joined query.
    [Theory]
    [InlineData(1, new string[0])]
    [InlineData(2, new[] { "alice" })]
    [InlineData(3, new[] { "alice" })]
    public async Task Terminal_userset_spends_a_level_of_depth(int depth, string[] expected)
    {
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "p1", "reviewer", "team", "t1", "member"),
                new RelationTuple("team", "t1", "member", "user", "alice"),
            ], [], UsersetDepthSchema);

        var result = (await engine.Lookup(new LookupSubjectRequest("project", "reviewer", "user", "p1", depth), default)).SubjectIds;

        result.Should().BeEquivalentTo(expected);
    }

    // u1 is two hops from p1, u2 three, u3 four (its last hop through the terminal team#member).
    [Theory]
    [InlineData(1, new string[0])]
    [InlineData(2, new[] { "u1" })]
    [InlineData(3, new[] { "u1", "u2" })]
    [InlineData(4, new[] { "u1", "u2", "u3" })]
    [InlineData(5, new[] { "u1", "u2", "u3" })]
    public async Task Userset_chain_spends_a_level_of_depth_per_hop(int depth, string[] expected)
    {
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "p1", "viewer", "group", "g1", "member"),
                new RelationTuple("group", "g1", "member", "user", "u1"),
                new RelationTuple("group", "g1", "member", "group", "g2", "member"),
                new RelationTuple("group", "g2", "member", "user", "u2"),
                new RelationTuple("group", "g2", "member", "team", "t1", "member"),
                new RelationTuple("team", "t1", "member", "user", "u3"),
            ], [], UsersetDepthSchema);

        var result = (await engine.Lookup(new LookupSubjectRequest("project", "viewer", "user", "p1", depth), default)).SubjectIds;

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
        var context = new Dictionary<string, object> {{key, value!}};
        var result = (await engine.Lookup(new LookupSubjectRequest("account",
            "withdraw", "user", reqAcc, context: context), default)).SubjectIds;

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

        var result = (await engine.Lookup(
            new LookupSubjectRequest("workspace", "delete", "group", "1"),
            CancellationToken.None)).SubjectIds;

        result.Should().BeEmpty();
    }

    // Regression coverage for LookupSubject entity scoping. A cascading hop (e.g. parent.is_member)
    // can dead-end and forward an EMPTY entity-id list to the next recursion level. The SQL builders
    // used to drop the entity_id predicate on an empty list and return every subject system-wide,
    // diverging from the entity-scoped Check engine. These run against every provider to catch the
    // SQL-specific defect.
    private const string OrganisationScopeSchema = """
        entity user {}
        entity organisation {
            relation parent @organisation;
            relation member @user;
            permission is_member := member or parent.is_member;
            permission direct_member := member;
        }
        """;

    [Fact]
    public async Task DirectMember_IsEntityScoped()
    {
        var engine = await CreateEngine(
        [
            new RelationTuple("organisation", "1", "member", "user", "alice"),
            new RelationTuple("organisation", "1", "member", "user", "bob"),
            new RelationTuple("organisation", "2", "member", "user", "carol"),
        ], [], OrganisationScopeSchema);

        var org1 = (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "1"), default)).SubjectIds;
        var org2 = (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "2"), default)).SubjectIds;
        var org999 = (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "999"), default)).SubjectIds;

        org1.Should().BeEquivalentTo("alice", "bob");
        org2.Should().BeEquivalentTo("carol");
        org999.Should().BeEmpty();
    }

    [Fact]
    public async Task CascadingMember_ResolvesParentChain_StaysScoped()
    {
        // org2 has org1 as parent. alice is a direct member of org1.
        var engine = await CreateEngine(
        [
            new RelationTuple("organisation", "2", "parent", "organisation", "1"),
            new RelationTuple("organisation", "1", "member", "user", "alice"),
            new RelationTuple("organisation", "3", "member", "user", "carol"),
        ], [], OrganisationScopeSchema);

        // is_member cascades through parent.is_member -> alice, but stays scoped (no carol from org3).
        (await engine.Lookup(new LookupSubjectRequest("organisation", "is_member", "user", "2"), default)).SubjectIds
            .Should().BeEquivalentTo("alice");

        // direct_member must NOT cross the parent edge.
        (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "2"), default)).SubjectIds
            .Should().BeEmpty();

        // A leaf org with no parent: is_member resolves only its own members (empty-hop must not leak).
        (await engine.Lookup(new LookupSubjectRequest("organisation", "is_member", "user", "1"), default)).SubjectIds
            .Should().BeEquivalentTo("alice");
    }

    [Fact]
    public async Task SoftDelete_StaysScoped_AndDeduplicated()
    {
        var sp = await CreateScope([], [], OrganisationScopeSchema);
        var writer = sp.GetRequiredService<IDataWriterProvider>();
        var engine = sp.GetRequiredService<ILookupSubjectEngine>();

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

        var result = (await engine.Lookup(new LookupSubjectRequest("organisation", "direct_member", "user", "1"), default)).SubjectIds;

        // alice (active) + dave (one active row among several deleted), still scoped to org1 (no carol).
        result.Should().BeEquivalentTo("alice", "dave");
        result.Should().NotContain("carol");
    }

    [Fact]
    public async Task LookupSubject_And_Check_Agree()
    {
        var sp = await CreateScope(
        [
            new RelationTuple("organisation", "2", "parent", "organisation", "1"),
            new RelationTuple("organisation", "1", "member", "user", "alice"),
            new RelationTuple("organisation", "1", "member", "user", "bob"),
            new RelationTuple("organisation", "2", "member", "user", "carol"),
            new RelationTuple("organisation", "3", "member", "user", "dave"),
        ], [], OrganisationScopeSchema);
        var lookup = sp.GetRequiredService<ILookupSubjectEngine>();
        var check = sp.GetRequiredService<ICheckEngine>();

        string[] subjects = ["alice", "bob", "carol", "dave"];
        string[] orgs = ["1", "2", "3"];

        foreach (var permission in new[] { "direct_member", "is_member" })
        foreach (var org in orgs)
        {
            var set = (await lookup.Lookup(new LookupSubjectRequest("organisation", permission, "user", org), default)).SubjectIds;
            foreach (var subject in subjects)
            {
                var inLookup = set.Contains(subject);
                var checkResult = (await check.Check(
                    new CheckRequest("organisation", org, permission, "user", subject), default)).Allowed;
                inLookup.Should().Be(checkResult,
                    $"LookupSubject and Check must agree for permission={permission} org={org} subject={subject}");
            }
        }
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

        var result = (await engine.Lookup(
            new LookupSubjectRequest("document", "non_owner", "user", "doc1"),
            CancellationToken.None)).SubjectIds;

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
        var result = (await engine.Lookup(
            new LookupSubjectRequest("workspace", "view", "user", "w1"),
            CancellationToken.None)).SubjectIds;

        // Assert — alice is reachable through both g1 and g2, but must appear exactly once.
        result.Should().BeEquivalentTo("alice");
    }

    // Quote, backslash, comma, apostrophe, non-ASCII, and a JSON-looking id.
    private static readonly string[] IdsNeedingEscaping = ["g\"1", "g\\2", "g,3", "g'4", "gé5", "[\"g6\"]"];

    private const string EscapingSchema = """
        entity user {}
        entity group {
            relation member @user @group#member;
        }
        entity doc {
            relation viewer @group#member;
            permission view := viewer;
        }
        """;

    [Fact]
    public async Task Lookup_WithIdsNeedingEscaping_ForwardsThemUnchanged()
    {
        // The group ids reach the database as one list (group.member is non-terminal, so the
        // groups found on doc d1 are forwarded as entity ids), and so do the user ids returned.
        var tuples = IdsNeedingEscaping.SelectMany(g => new[]
        {
            new RelationTuple("doc", "d1", "viewer", "group", g, "member"),
            new RelationTuple("group", g, "member", "user", "u" + g),
        }).ToArray();
        var engine = await CreateEngine(tuples, [], EscapingSchema);

        var result = (await engine.Lookup(new LookupSubjectRequest("doc", "view", "user", "d1"), default)).SubjectIds;

        result.Should().BeEquivalentTo(IdsNeedingEscaping.Select(g => "u" + g));
    }

    [Fact]
    public async Task LookupSubject_UnionWithHeterogeneousSubjectType_PrunesDeadBranchCorrectly()
    {
        const string schema = """
            entity user {}
            entity service_account {}
            entity organization {
                relation admin @user;
                relation bot @service_account;
                permission access := admin or bot;
            }
            """;

        var engine = await CreateEngine(
            [
                new RelationTuple("organization", "org1", "admin", "user", "alice"),
                new RelationTuple("organization", "org1", "bot", "service_account", "svc1"),
            ], [], schema);

        // subjectType=user: the "bot" branch (service_account-only) is statically unreachable
        // and gets pruned before any task is spawned for it. svc1 must never appear in a
        // user-typed lookup, and alice (via admin) must still be found correctly despite the
        // sibling being pruned out of the Union.
        var result = (await engine.Lookup(
            new LookupSubjectRequest("organization", "access", "user", "org1"),
            CancellationToken.None)).SubjectIds;

        result.Should().BeEquivalentTo("alice");
    }

    [Fact]
    public async Task LookupSubject_IntersectWithHeterogeneousSubjectType_PrunesToEmpty()
    {
        const string schema = """
            entity user {}
            entity service_account {}
            entity organization {
                relation admin @user;
                relation bot @service_account;
                permission strict_access := admin and bot;
            }
            """;

        var engine = await CreateEngine(
            [
                new RelationTuple("organization", "org1", "admin", "user", "alice"),
                new RelationTuple("organization", "org1", "bot", "service_account", "svc1"),
            ], [], schema);

        // subjectType=user: "bot" is statically unreachable for a user subject, so the whole
        // Intersect is provably empty regardless of what tuples exist — even though org1 has
        // both an admin=alice AND a bot=svc1 tuple, a user can never satisfy "bot".
        var result = (await engine.Lookup(
            new LookupSubjectRequest("organization", "strict_access", "user", "org1"),
            CancellationToken.None)).SubjectIds;

        result.Should().BeEmpty();
    }

    private const string RecursiveUsersetSchema = """
        entity user {}
        entity group {
            relation member @user @group#member;
        }
        entity folder {
            relation owner @group#member;
            permission view := owner;
        }
        """;

    // g1 contains g2's members, g2 contains alice; g3 and g4 contain each other's members (a
    // membership cycle) and dave belongs to g4. folder:nested is owned by g1, folder:cyclic by g3.
    private static readonly RelationTuple[] RecursiveUsersetTuples =
    [
        new("group", "g1", "member", "group", "g2", "member"),
        new("group", "g2", "member", "user", "alice"),
        new("group", "g3", "member", "group", "g4", "member"),
        new("group", "g4", "member", "group", "g3", "member"),
        new("group", "g4", "member", "user", "dave"),
        new("folder", "nested", "owner", "group", "g1", "member"),
        new("folder", "cyclic", "owner", "group", "g3", "member"),
    ];

    [Theory]
    [InlineData("nested", new[] { "alice" })]
    [InlineData("cyclic", new[] { "dave" })]
    public async Task LookupSubject_through_recursive_usersets_terminates_with_the_right_answer(string folder,
        string[] expected)
    {
        var engine = await CreateEngine(RecursiveUsersetTuples, [], RecursiveUsersetSchema);

        var result = (await engine.Lookup(new LookupSubjectRequest("folder", "view", "user", folder), default)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30))).SubjectIds;

        result.Should().BeEquivalentTo(expected);
    }

    // A subject's relation is part of what a tuple grants: a direct grant or a tuple-to-userset
    // hop reads only tuples whose subject has no relation, and a userset @T#R only tuples whose
    // subject is T#R, so each form of a subject type on one relation is read on its own.
    private const string SubjectRelationSchema = """
        entity user {}
        entity service_account {}
        entity group {
            relation member @user;
            relation admin @user;
            relation sub @group;
        }
        entity team {
            relation member @user @team#member;
            relation lead @user @team#member;
        }
        entity squad {
            relation member @user;
        }
        entity doc {
            relation viewer @group#member @group#admin;
            relation reader @team#member @team#lead;
            relation editor @group @group#sub;
            relation parent @group;
            relation team @team;
            relation squad_viewer @squad#member;
            relation owner @user;
            relation manager @user;
            permission parent_view := parent.member;
            permission team_view := team.member;
            permission manage := owner or manager;
        }
        """;

    public static TheoryData<string, RelationTuple[], LookupSubjectRequest, string[]> SubjectRelationCases => new()
    {
        {
            "joined userset reads only the userset's own relation",
            [
                new("doc", "d1", "viewer", "group", "g1", "admin"),
                new("group", "g1", "member", "user", "u1"),
                new("group", "g1", "admin", "user", "u2"),
            ],
            new LookupSubjectRequest("doc", "viewer", "user", "d1"),
            ["u2"]
        },
        {
            "joined userset reads only direct final subjects",
            [
                new("doc", "d1", "viewer", "group", "g1", "member"),
                new("group", "g1", "member", "user", "u1", "member"),
                new("group", "g1", "member", "user", "u2"),
            ],
            new LookupSubjectRequest("doc", "viewer", "user", "d1"),
            ["u2"]
        },
        {
            "joined userset reads only final subjects of the requested type",
            [
                new("doc", "d1", "squad_viewer", "squad", "s1", "member"),
                new("squad", "s1", "member", "user", "u1"),
                new("squad", "s1", "member", "service_account", "sa1"),
            ],
            new LookupSubjectRequest("doc", "squad_viewer", "user", "d1"),
            ["u1"]
        },
        {
            "nested userset reads only the userset's own relation",
            [
                new("doc", "d1", "reader", "team", "t1", "lead"),
                new("team", "t1", "member", "user", "u1"),
                new("team", "t1", "lead", "user", "u2"),
            ],
            new LookupSubjectRequest("doc", "reader", "user", "d1"),
            ["u2"]
        },
        {
            "a userset of the requested subject type is followed through its own relation",
            [
                new("doc", "d1", "editor", "group", "g1", "sub"),
                new("group", "g1", "sub", "group", "g2"),
                new("doc", "d1", "editor", "group", "g3"),
            ],
            new LookupSubjectRequest("doc", "editor", "group", "d1"),
            ["g2", "g3"]
        },
        {
            "joined tuple-to-userset reads only subjects without a relation",
            [
                new("doc", "d1", "parent", "group", "g1", "admin"),
                new("doc", "d1", "parent", "group", "g2"),
                new("group", "g1", "member", "user", "u1"),
                new("group", "g2", "member", "user", "u2"),
            ],
            new LookupSubjectRequest("doc", "parent_view", "user", "d1"),
            ["u2"]
        },
        {
            "nested tuple-to-userset reads only subjects without a relation",
            [
                new("doc", "d1", "team", "team", "t1", "lead"),
                new("doc", "d1", "team", "team", "t2"),
                new("team", "t1", "member", "user", "u1"),
                new("team", "t2", "member", "user", "u2"),
            ],
            new LookupSubjectRequest("doc", "team_view", "user", "d1"),
            ["u2"]
        },
        {
            "sibling relations read only subjects without a relation",
            [
                new("doc", "d1", "owner", "user", "u1"),
                new("doc", "d1", "manager", "user", "u2", "member"),
            ],
            new LookupSubjectRequest("doc", "manage", "user", "d1"),
            ["u1"]
        },
    };

    [Theory]
    [MemberData(nameof(SubjectRelationCases))]
    public async Task Lookup_reads_only_tuples_whose_subject_relation_the_schema_path_names(string because,
        RelationTuple[] tuples, LookupSubjectRequest request, string[] expected)
    {
        var engine = await CreateEngine(tuples, [], SubjectRelationSchema);

        var result = (await engine.Lookup(request, default)).SubjectIds;

        result.Should().BeEquivalentTo(expected, because);
    }

    [Fact]
    public async Task LookupSubject_reports_the_snap_token_it_read_at()
    {
        var (services, first, latest) = await WriteTwice();
        var engine = services.GetRequiredService<ILookupSubjectEngine>();

        var result = await engine.Lookup(new LookupSubjectRequest("workspace", "delete", "user", "w2"), default);
        result.SubjectIds.Should().BeEquivalentTo(["u1"]);
        result.SnapToken.Should().Be(latest);

        var pinned = await engine.Lookup(
            new LookupSubjectRequest("workspace", "delete", "user", "w2") { SnapToken = first }, default);
        pinned.SubjectIds.Should().BeEmpty();
        pinned.SnapToken.Should().Be(first);
    }

    [Fact]
    public async Task LookupSubject_that_reads_nothing_reports_no_snap_token()
    {
        var (services, _, _) = await WriteTwice();
        var engine = services.GetRequiredService<ILookupSubjectEngine>();

        // No relation of workspace.delete can hold a group, so the engine answers without reading.
        var result = await engine.Lookup(new LookupSubjectRequest("workspace", "delete", "group", "w1"), default);

        result.SubjectIds.Should().BeEmpty();
        result.SnapToken.Should().BeNull();
    }

    private async ValueTask<(IServiceProvider Services, SnapToken First, SnapToken Latest)> WriteTwice()
    {
        var serviceProvider = CreateServiceProvider();
        _ownedResources.Add(serviceProvider);
        var scope = serviceProvider.CreateAsyncScope();
        _ownedResources.Add(scope);
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        var first = await writer.Write([new RelationTuple("workspace", "w1", "owner", "user", "u1")], [], default);
        var latest = await writer.Write([new RelationTuple("workspace", "w2", "owner", "user", "u1")], [], default);
        return (scope.ServiceProvider, first, latest);
    }
}
