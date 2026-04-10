using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Schemas;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Data;
using Valtuutus.Core.Data;
using Valtuutus.Core.Lang;

namespace Valtuutus.Tests.Shared;

public abstract class BaseLookupEntityEngineSpecs : IAsyncLifetime
{
    protected BaseLookupEntityEngineSpecs(IDatabaseFixture fixture)
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
            .AddConcurrentQueryLimit(5);

        return services.BuildServiceProvider();
    }

    private async ValueTask<ILookupEntityEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        string? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var lookupEntityEngine = scope.ServiceProvider.GetRequiredService<ILookupEntityEngine>();
        if (tuples.Length == 0 && attributes.Length == 0) return lookupEntityEngine;
        var provider = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await provider.Write(tuples, attributes, default);
        return lookupEntityEngine;
    }

    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        TopLevelChecks = LookupEntityEngineSpecList.TopLevelChecks;

    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupEntityRequest request, IReadOnlyList<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.EntityIds.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        IndirectRelationLookup = LookupEntityEngineSpecList.IndirectRelationLookup;

    [Theory]
    [MemberData(nameof(IndirectRelationLookup))]
    public async Task IndirectRelationLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, IReadOnlyList<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.EntityIds.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        SimplePermissionLookup = LookupEntityEngineSpecList.SimplePermissionLookup;

    [Theory]
    [MemberData(nameof(SimplePermissionLookup))]
    public async Task SimplePermissionLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, IReadOnlyList<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.EntityIds.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        IntersectWithRelationAndAttributePermissionLookup =
            LookupEntityEngineSpecList.IntersectWithRelationAndAttributePermissionLookup;

    [Theory]
    [MemberData(nameof(IntersectWithRelationAndAttributePermissionLookup))]
    public async Task IntersectWithRelationAndAttributeLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, IReadOnlyList<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.EntityIds.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        IntersectAttributeExpressionWithOtherNodesLookup =
            LookupEntityEngineSpecList.IntersectAttributeExpressionWithOtherNodes;

    [Theory]
    [MemberData(nameof(IntersectAttributeExpressionWithOtherNodesLookup))]
    public async Task IntersectAttributeExpressionWithOtherNodesLookupShouldReturnExpectedEntities(
        RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, IReadOnlyList<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.EntityIds.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task TestStringBasedAttributeExpression()
    {
        // arrange
        var schema = @"
            entity user {}
            entity workspace {
                attribute status string;
                permission edit:= isActiveStatus(status);
            }
            fn isActiveStatus(status string) => status == ""active"";
        ";

        // act
        var engine = await CreateEngine([], [
            new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "status",
                JsonValue.Create("active")!),
            new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "status",
                JsonValue.Create("active")!),
            new AttributeTuple(TestsConsts.Workspaces.Identifier, "3", "status",
                JsonValue.Create("archived")!),
        ], schema);

        // Act
        var result = await engine.LookupEntity(new LookupEntityRequest(TestsConsts.Workspaces.Identifier,
            "edit", "user", "1"), default);

        // assert
        result.EntityIds.Should().BeEquivalentTo(TestsConsts.Workspaces.PublicWorkspace, TestsConsts.Workspaces.PrivateWorkspace);
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
                new RelationTuple("account", "1", "owner", TestsConsts.Users.Identifier, "1"),
                new RelationTuple("account", "2", "owner", TestsConsts.Users.Identifier, "1")
            ],
            [
                new AttributeTuple("account", "1", "balance",
                    JsonValue.Create(872.54m)),
                new AttributeTuple("account", "2", "balance",
                    JsonValue.Create(12.11m)),
            ], schema);

        // Act
        var result = await engine.LookupEntity(new LookupEntityRequest("account",
            "withdraw", "user", "1"), default);

        // assert
        result.EntityIds.Should().BeEquivalentTo("1");
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        UnionRelationDepthLimit = LookupEntityEngineSpecList.UnionRelationDepthLimit;

    [Theory]
    [MemberData(nameof(UnionRelationDepthLimit))]
    public async Task LookupEntityWithDepthLimit(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, IReadOnlyList<string> expected)
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
        var result = await engine.LookupEntity(request, default);

        // Assert
        result.EntityIds.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<string, decimal?, HashSet<string>> ContextAccessTheoryData = new()
    {
        { "withdraw_amount", 500.0m, ["1"] },
        { "withdraw_amount", 1000.0m, [] },
        { "withdraw_amount", 872.54m, ["1"] },
        { "withdraw_amount", 100.0m, ["1", "2"] },
        { "withdraw_amount", null, [] },
        { "amount", 500.0m, [] }
    };

    [Theory, MemberData(nameof(ContextAccessTheoryData))]
    public async Task Test_function_call_with_context_arg(string key, decimal? value, HashSet<string> expected)
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
                new RelationTuple("account", "1", "owner", TestsConsts.Users.Identifier, "1"),
                new RelationTuple("account", "2", "owner", TestsConsts.Users.Identifier, "1")
            ],
            [
                new AttributeTuple("account", "1", "balance",
                    JsonValue.Create(872.54m)),
                new AttributeTuple("account", "2", "balance",
                    JsonValue.Create(120.11m)),
            ], schema);

        // Act
        var context = new Dictionary<string, object> { { key, value } };
        var result = await engine.LookupEntity(new LookupEntityRequest("account",
            "withdraw", "user", "1", context: context), default);

        // assert
        result.EntityIds.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Test_permission_with_attribute_when_user_has_no_relation_and_attribute_is_enough_to_pass()
    {
        var schema = """
                     entity user { }

                     entity portfolio {
                         relation owner @user;
                         relation admin @user;
                         relation member @user;
                         attribute public bool;
                         permission create_project := owner or admin;
                         permission assign_user := owner or admin;
                         permission view := public or owner or admin or member;
                     }

                     entity group {
                         relation member @user;
                     }

                     entity project {
                         relation portfolio @portfolio ;
                         relation admin @user @group#member;
                         relation member @user @group#member;
                         attribute status string;
                         permission create_task := (portfolio.owner or portfolio.admin
                                         or admin or member) and isActiveStatus(status);
                         permission edit := (portfolio.owner or portfolio.admin or admin) and isActiveStatus(status);
                         permission view := admin or member or portfolio.view;
                     }

                     entity task {
                         relation project @project;
                         permission view := project.view;
                     }

                     fn isActiveStatus(status string) => status != "Archived";
                     """;

        // act
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "project-p1", "portfolio", "portfolio", "portfolio-1"),
                new RelationTuple("project", "project-p1-2", "portfolio", "portfolio", "portfolio-1"),
                new RelationTuple("project", "project-p3", "portfolio", "portfolio", "portfolio-3"),
            ],
            [
                new AttributeTuple("portfolio", "portfolio-1", "public",
                    JsonValue.Create(true)),
                new AttributeTuple("portfolio", "portfolio-2", "public",
                    JsonValue.Create(false)),
                new AttributeTuple("portfolio", "portfolio-3", "public",
                    JsonValue.Create(true)),
            ], schema);

        //
        var result = await engine.LookupEntity(new LookupEntityRequest("project",
            "view", "user", "alice"), CancellationToken.None);

        result.EntityIds.Should().BeEquivalentTo(["project-p1", "project-p1-2", "project-p3"]);
    }

    [Fact]
    public async Task TypeGuard_ShouldReturnEmpty_WhenSubjectTypeNotAllowedInRelation()
    {
        // The 'owner' relation on 'workspace' only allows 'user', not 'group'.
        // LookupEntity for subject type 'group' should return nothing without a DB call.
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

        var result = await engine.LookupEntity(
            new LookupEntityRequest("workspace", "delete", "group", "admins"),
            CancellationToken.None);

        result.EntityIds.Should().BeEmpty();
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        ScopedLookup = LookupEntityEngineSpecList.ScopedLookup;

    [Theory]
    [MemberData(nameof(ScopedLookup))]
    public async Task ScopedLookupShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupEntityRequest request, IReadOnlyList<string> expected)
    {
        var engine = await CreateEngine(tuples, attributes);
        var result = await engine.LookupEntity(request, default);
        result.EntityIds.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>, string?>
        PaginatedLookup = LookupEntityEngineSpecList.PaginatedLookup;

    [Theory]
    [MemberData(nameof(PaginatedLookup))]
    public async Task PaginatedLookupShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupEntityRequest request, IReadOnlyList<string> expected, string? expectedToken)
    {
        var engine = await CreateEngine(tuples, attributes);
        var result = await engine.LookupEntity(request, default);
        result.EntityIds.Should().BeEquivalentTo(expected);
        result.ContinuationToken.Should().Be(expectedToken);
    }

    [Fact]
    public async Task PaginatedLookupShouldPageCorrectly()
    {
        var tuples = new RelationTuple[]
        {
            new("project", "proj1", "member", "user", "alice"),
            new("task", "aaa", "parent", "project", "proj1"),
            new("task", "bbb", "parent", "project", "proj1"),
            new("task", "ccc", "parent", "project", "proj1"),
            new("task", "ddd", "parent", "project", "proj1"),
            new("task", "eee", "parent", "project", "proj1"),
        };
        var engine = await CreateEngine(tuples, []);

        var req = new LookupEntityRequest("task", "view", "user", "alice")
        {
            Scope = new EntityScope("parent", "project", "proj1"),
            PageSize = 2
        };

        var page1 = await engine.LookupEntity(req, default);
        page1.EntityIds.Should().BeEquivalentTo(["aaa", "bbb"]);
        page1.ContinuationToken.Should().NotBeNullOrEmpty();

        var page2 = await engine.LookupEntity(req with { ContinuationToken = page1.ContinuationToken }, default);
        page2.EntityIds.Should().BeEquivalentTo(["ccc", "ddd"]);
        page2.ContinuationToken.Should().NotBeNullOrEmpty();

        var page3 = await engine.LookupEntity(req with { ContinuationToken = page2.ContinuationToken }, default);
        page3.EntityIds.Should().BeEquivalentTo(["eee"]);
        page3.ContinuationToken.Should().BeNull();
    }

    [Fact]
    public async Task ScopedLookupWithScopeRelationNotInSchemaShouldThrow()
    {
        var engine = await CreateEngine([], []);
        var req = new LookupEntityRequest("task", "view", "user", "alice")
        {
            Scope = new EntityScope("nonexistent_relation", "project", "proj1")
        };
        var act = async () => await engine.LookupEntity(req, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PaginationWithMalformedCursorShouldThrow()
    {
        var engine = await CreateEngine([], []);
        var req = new LookupEntityRequest("task", "view", "user", "alice")
        {
            PageSize = 2,
            ContinuationToken = "!!!not-valid-base64!!!"
        };
        var act = async () => await engine.LookupEntity(req, default);
        await act.Should().ThrowAsync<FormatException>();
    }

    /// <summary>
    /// Exercises the CheckLeafExp path with scope: a union permission contains an attribute-expression
    /// leaf alongside a relation leaf. CheckLeafExp calls GetAttributes(filter, scope, ct) to restrict
    /// attribute results to the scoped entity set. Without scope, doc3 (org=org2, published=true) would
    /// appear in results; with scope restricted to org1 it must be excluded.
    /// </summary>
    [Fact]
    public async Task ScopedLookup_AttributeExpressionInUnion_ExcludesOutOfScopeEntities()
    {
        const string schema = """
            entity user {}
            entity org { relation member @user; }
            entity document {
                relation org @org;
                attribute published bool;
                permission view := org.member or isPublished(published);
            }
            fn isPublished(published bool) => published == true;
            """;

        var engine = await CreateEngine(
            [
                new RelationTuple("org", "org1", "member", "user", "alice"),
                new RelationTuple("document", "doc1", "org", "org", "org1"),
                new RelationTuple("document", "doc2", "org", "org", "org1"),
                new RelationTuple("document", "doc3", "org", "org", "org2"),
            ],
            [
                new AttributeTuple("document", "doc1", "published", JsonValue.Create(true)),
                new AttributeTuple("document", "doc2", "published", JsonValue.Create(false)),
                new AttributeTuple("document", "doc3", "published", JsonValue.Create(true)),
            ],
            schema);

        var result = await engine.LookupEntity(
            new LookupEntityRequest("document", "view", "user", "alice")
            {
                Scope = new EntityScope("org", "org", "org1")
            },
            CancellationToken.None);

        // doc1: in scope (org1) + alice is member of org1 → YES
        // doc2: in scope (org1) + alice is member of org1 → YES (via org.member); published=false so not via isPublished
        // doc3: out of scope (org2) → excluded even though published=true
        result.EntityIds.Should().BeEquivalentTo(["doc1", "doc2"]);
    }

    /// <summary>
    /// Exercises LookupAttribute with scope. When the requested permission name matches an attribute
    /// directly (RelationType.Attribute), the engine resolves scope via GetRelationsWithSubjectsIds
    /// then filters attribute results to the scoped entity set.
    /// </summary>
    [Fact]
    public async Task ScopedLookup_DirectAttributePermission_FiltersToScope()
    {
        const string schema = """
            entity org {}
            entity document {
                relation org @org;
                attribute published bool;
            }
            """;

        var engine = await CreateEngine(
            [
                new RelationTuple("document", "doc1", "org", "org", "org1"),
                new RelationTuple("document", "doc2", "org", "org", "org1"),
                new RelationTuple("document", "doc3", "org", "org", "org2"),
            ],
            [
                new AttributeTuple("document", "doc1", "published", JsonValue.Create(true)),
                new AttributeTuple("document", "doc2", "published", JsonValue.Create(false)),
                new AttributeTuple("document", "doc3", "published", JsonValue.Create(true)),
            ],
            schema);

        // "published" resolves as RelationType.Attribute → LookupAttribute path
        var result = await engine.LookupEntity(
            new LookupEntityRequest("document", "published", "user", "alice")
            {
                Scope = new EntityScope("org", "org", "org1")
            },
            CancellationToken.None);

        // doc1: in scope (org1), published=true → YES
        // doc2: in scope (org1), published=false → NO
        // doc3: out of scope (org2) → excluded even though published=true
        result.EntityIds.Should().BeEquivalentTo(["doc1"]);
    }

    /// <summary>
    /// Exercises LookupIntersectionConstrained with scope: the default schema's project.edit uses
    /// (parent.admin or team.member) AND isActiveStatus(status). With a scope on parent workspace,
    /// only projects in that workspace are considered, and the attribute filter (status==1) further
    /// narrows the results.
    /// </summary>
    [Fact]
    public async Task ScopedLookup_IntersectionWithAttributeAndRelation_FiltersCorrectly()
    {
        var engine = await CreateEngine(
            [
                new RelationTuple("workspace", "ws1", "admin", "user", "alice"),
                new RelationTuple("project", "p1", "parent", "workspace", "ws1"),
                new RelationTuple("project", "p2", "parent", "workspace", "ws1"),
                new RelationTuple("project", "p3", "parent", "workspace", "ws2"),
            ],
            [
                new AttributeTuple("project", "p1", "status", JsonValue.Create(1)),
                new AttributeTuple("project", "p2", "status", JsonValue.Create(2)),
                new AttributeTuple("project", "p3", "status", JsonValue.Create(1)),
            ]);

        // project.edit := (parent.admin or team.member) and isActiveStatus(status)
        // Scope: only projects with parent relation pointing to ws1
        var result = await engine.LookupEntity(
            new LookupEntityRequest("project", "edit", "user", "alice")
            {
                Scope = new EntityScope("parent", "workspace", "ws1")
            },
            CancellationToken.None);

        // p1: in scope (ws1), alice is admin of ws1 → parent.admin YES, status=1 → isActiveStatus YES → YES
        // p2: in scope (ws1), alice is admin of ws1 → parent.admin YES, status=2 → isActiveStatus NO → NO
        // p3: out of scope (ws2) → excluded even though status=1
        result.EntityIds.Should().BeEquivalentTo(["p1"]);
    }

    /// <summary>
    /// Exercises paginated lookup without scope: verifies ContinuationToken is null when the total
    /// result count exactly equals PageSize.
    /// </summary>
    [Fact]
    public async Task PaginatedLookup_ExactlyPageSize_ReturnsNullToken()
    {
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "proj1", "member", "user", "alice"),
                new RelationTuple("task", "aaa", "parent", "project", "proj1"),
                new RelationTuple("task", "bbb", "parent", "project", "proj1"),
            ], []);

        var req = new LookupEntityRequest("task", "view", "user", "alice") { PageSize = 2 };
        var result = await engine.LookupEntity(req, default);

        result.EntityIds.Should().BeEquivalentTo(["aaa", "bbb"]);
        result.ContinuationToken.Should().BeNull();
    }

    /// <summary>
    /// Exercises cursor pagination resumption: second page using token from first page returns
    /// exactly the remaining items and a null next-page token.
    /// </summary>
    [Fact]
    public async Task PaginatedLookup_SecondPage_ReturnsRemainingItems()
    {
        var engine = await CreateEngine(
            [
                new RelationTuple("project", "proj1", "member", "user", "alice"),
                new RelationTuple("task", "aaa", "parent", "project", "proj1"),
                new RelationTuple("task", "bbb", "parent", "project", "proj1"),
                new RelationTuple("task", "ccc", "parent", "project", "proj1"),
            ], []);

        var req = new LookupEntityRequest("task", "view", "user", "alice") { PageSize = 2 };
        var page1 = await engine.LookupEntity(req, default);
        page1.EntityIds.Should().BeEquivalentTo(["aaa", "bbb"]);
        page1.ContinuationToken.Should().NotBeNullOrEmpty();

        var page2 = await engine.LookupEntity(req with { ContinuationToken = page1.ContinuationToken }, default);
        page2.EntityIds.Should().BeEquivalentTo(["ccc"]);
        page2.ContinuationToken.Should().BeNull();
    }

    [Fact]
    public async Task LookupEntity_Not_Relation_Returns_Entities_User_Does_Not_Own()
    {
        const string schema = """
            entity user {}
            entity document {
                relation owner @user;
                permission non_owner := not(owner);
            }
            """;

        var engine = await CreateEngine(
            [
                new RelationTuple("document", "doc1", "owner", "user", "alice"),
                new RelationTuple("document", "doc2", "owner", "user", "bob"),
                new RelationTuple("document", "doc3", "owner", "user", "bob"),
            ], [], schema);

        var result = await engine.LookupEntity(
            new LookupEntityRequest("document", "non_owner", "user", "alice"),
            CancellationToken.None);

        result.EntityIds.Should().BeEquivalentTo(["doc2", "doc3"]);
    }

    [Fact]
    public async Task LookupEntity_Not_Inside_And_Returns_Entities_Viewer_But_Not_Owner()
    {
        const string schema = """
            entity user {}
            entity document {
                relation owner @user;
                relation viewer @user;
                permission restricted_view := viewer and not(owner);
            }
            """;

        var engine = await CreateEngine(
            [
                new RelationTuple("document", "doc1", "owner", "user", "alice"),
                new RelationTuple("document", "doc1", "viewer", "user", "alice"),
                new RelationTuple("document", "doc2", "viewer", "user", "alice"),
                new RelationTuple("document", "doc3", "viewer", "user", "alice"),
                new RelationTuple("document", "doc3", "owner", "user", "alice"),
            ], [], schema);

        var result = await engine.LookupEntity(
            new LookupEntityRequest("document", "restricted_view", "user", "alice"),
            CancellationToken.None);

        // doc1 and doc3: alice is both viewer and owner → fails not(owner)
        // doc2: alice is viewer but not owner → passes
        result.EntityIds.Should().BeEquivalentTo(["doc2"]);
    }

    [Fact]
    public async Task LookupEntity_Not_Compound_Expression_Returns_Entities_User_Has_Neither_Relation()
    {
        const string schema = """
            entity user {}
            entity document {
                relation owner @user;
                relation editor @user;
                permission non_contributor := not(owner or editor);
            }
            """;

        var engine = await CreateEngine(
            [
                new RelationTuple("document", "doc1", "owner", "user", "alice"),
                new RelationTuple("document", "doc2", "editor", "user", "alice"),
                new RelationTuple("document", "doc3", "owner", "user", "bob"),
                new RelationTuple("document", "doc4", "editor", "user", "bob"),
            ], [], schema);

        var result = await engine.LookupEntity(
            new LookupEntityRequest("document", "non_contributor", "user", "alice"),
            CancellationToken.None);

        // doc1: alice is owner → excluded; doc2: alice is editor → excluded
        // doc3 and doc4: alice has neither relation → included
        result.EntityIds.Should().BeEquivalentTo(["doc3", "doc4"]);
    }
}
