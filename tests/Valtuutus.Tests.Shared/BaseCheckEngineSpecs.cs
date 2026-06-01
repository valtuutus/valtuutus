using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Schemas;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data;
using Valtuutus.Core.Data;
using Valtuutus.Core.Lang;

namespace Valtuutus.Tests.Shared;

public abstract class BaseCheckEngineSpecs : IAsyncLifetime
{

    protected BaseCheckEngineSpecs(IDatabaseFixture fixture)
    {
        Fixture = fixture;
    }

    protected IDatabaseFixture Fixture { get; }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);
    
    private ServiceProvider CreateServiceProvider(string? schema = null)
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(schema ?? TestsConsts.DefaultSchema);
        
        AddSpecificProvider(services)
            .AddConcurrentQueryLimit(3);

        return services.BuildServiceProvider();
    }

    private async ValueTask<ICheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        string? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var checkEngine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
        if(tuples.Length == 0 && attributes.Length == 0) return checkEngine;
        var provider = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await provider.Write(tuples, attributes, default);
        return checkEngine;
    }

    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }


    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> TopLevelChecks =
        CheckEngineSpecList.TopLevelChecks;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionRelationsData =
        CheckEngineSpecList.UnionRelationsData;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> IntersectionRelationsData =
        CheckEngineSpecList.IntersectionRelationsData;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionRelationsAttributesData =
        CheckEngineSpecList.UnionRelationsAttributesData;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool>
        IntersectionRelationsAttributesData =
            CheckEngineSpecList.IntersectionRelationsAttributesData;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> NestedRelationData =
        CheckEngineSpecList.NestedRelationData;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionOfDirectAndNestedRelationData =
        CheckEngineSpecList.UnionOfDirectAndNestedRelationData;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool>
        IntersectionOfDirectAndNestedRelationData =
            CheckEngineSpecList.IntersectionOfDirectAndNestedRelationData;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> NestedPermissionsData =
        CheckEngineSpecList.NestedPermissionsData;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool>
        IntersectionBetweenAttributeExpAndOtherNodes =
            CheckEngineSpecList.IntersectionBetweenAttributeExpAndOtherNodes;

    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool>
        UnionRelationDepthLimit =
            CheckEngineSpecList.UnionRelationDepthLimit;


    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        CheckRequest request, bool expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);


        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }


    [Theory]
    [MemberData(nameof(UnionRelationsData))]
    public async Task CheckingSimpleUnionOfRelationsShouldReturnExpected(RelationTuple[] tuples,
        AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity project {
                relation member @user;
                relation admin @user;
                permission view := member or admin;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }


    [Theory]
    [MemberData(nameof(IntersectionRelationsData))]
    public async Task CheckingSimpleIntersectionOfRelationsShouldReturnExpected(RelationTuple[] tuples,
        AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity project {
                relation owner @user;
                relation whatever @user;
                permission delete := owner and whatever;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(UnionRelationsAttributesData))]
    public async Task CheckingSimpleUnionOfRelationsAndAttributesShouldReturnExpected(RelationTuple[] tuples,
        AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity project {
                relation member @user;
                relation admin @user;
                attribute public bool;
                permission view := member or public;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }


    [Theory]
    [MemberData(nameof(IntersectionRelationsAttributesData))]
    public async Task CheckingSimpleIntersectionOfRelationsAndAttributesShouldReturnExpected(RelationTuple[] tuples,
        AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity project {
                relation member @user;
                attribute public bool;
                permission comment := member and public;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(NestedRelationData))]
    public async Task CheckingSimpleNestedRelationShouldReturnExpected(RelationTuple[] tuples,
        AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity workspace {
                relation admin @user;
                relation member @user;
            }

            entity project {
                relation parent @workspace;
                permission delete := parent.admin;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }


    [Theory]
    [MemberData(nameof(UnionOfDirectAndNestedRelationData))]
    public async Task CheckingUnionOfDirectAndNestedRelationsShouldReturnExpected(RelationTuple[] tuples,
        AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity workspace {
                relation admin @user;
                relation member @user;
            }

            entity project {
                relation admin @user;
                relation parent @workspace;
                permission delete := parent.admin or admin;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(IntersectionOfDirectAndNestedRelationData))]
    public async Task CheckingIntersectionOfDirectAndNestedRelationsShouldReturnExpected(RelationTuple[] tuples,
        AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity workspace {
                relation admin @user;
                relation member @user;
            }

            entity project {
                relation admin @user;
                relation parent @workspace;
                permission delete := parent.admin and admin;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(NestedPermissionsData))]
    public async Task CheckingNestedPermissionsShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes,
        CheckRequest request, bool expected)
    {
        // Arrange
        var schema = @"
            entity user {}
            entity workspace {
                relation admin @user;
                relation member @user;
                permission view := admin or member;
            }

            entity project {
                relation admin @user;
                relation parent @workspace;
                permission view := parent.view;
            }
        ";
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(UnionRelationDepthLimit))]
    public async Task CheckingDepthLimit(RelationTuple[] tuples, AttributeTuple[] attributes,
        CheckRequest request, bool expected)
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
        var result = await engine.Check(request, default);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(IntersectionBetweenAttributeExpAndOtherNodes))]
    public async Task CheckIntersectionBetweenAttributeExpAndOtherNodes(RelationTuple[] tuples,
        AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Check(request, default);

        // assert
        result.Should().Be(expected);
    }


    [Fact]
    public async Task EmptyDataShouldReturnFalseOnPermissions()
    {
        // Arrange
        var engine = await CreateEngine([], []);


        // Act
        var result =
            await engine.Check(
                new CheckRequest
                {
                    EntityType = "workspace",
                    Permission = "view",
                    EntityId = "1",
                    SubjectId = "1",
                    SubjectType = "user",
                    SnapToken = null
                }, default);


        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SubjectPermissionsWhenNoPermissionsShouldReturnEmpty()
    {
        // Arrange
        var schema = @"
            entity user {}
            entity workspace {
                relation admin @user;
                relation member @user;
            }

            entity project {
                relation admin @user;
                relation parent @workspace;
                permission view := admin or parent;
            }
        ";
        var engine = await CreateEngine([], [], schema);


        // Act
        var result = await engine.SubjectPermission(
            new SubjectPermissionRequest
            {
                EntityType = "workspace", EntityId = "1", SubjectType = "user", SubjectId = "1"
            }, default);


        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SubjectPermissionShouldListAllPermissions()
    {
        // arrange
        var schema = @"
            entity user {}
            entity workspace {
                attribute public bool;
                {replace}
            }
        ";

        var permissionBuilder = new StringBuilder();

        for (int i = 0; i < 50; i++)
        {
            permissionBuilder.AppendLine($"permission permission_{i} := public;");
        }

        // act
        var engine = await CreateEngine([], [], schema.Replace("{replace}", permissionBuilder.ToString()));

        // Act
        var result = await engine.SubjectPermission(
            new SubjectPermissionRequest
            {
                EntityType = "workspace", EntityId = "1", SubjectType = "user", SubjectId = "1"
            }, default);

        // assert
        await Verify(result);
    }


    [Fact]
    public async Task SubjectPermissionShouldEvaluatePermissions()
    {
        // arrange
        var schema = @"
            entity user {}
            entity workspace {
                attribute public bool;
                {replace}
            }
        ";

        var permissionBuilder = new StringBuilder();

        for (int i = 0; i < 50; i++)
        {
            permissionBuilder.AppendLine($"permission permission_{i} := public;");
        }
        
        // act
        var engine = await CreateEngine([], [
            new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public",
                JsonValue.Create(true))
        ], schema.Replace("{replace}", permissionBuilder.ToString()));

        // Act
        var result = await engine.SubjectPermission(
            new SubjectPermissionRequest
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                SubjectType = "user",
                SubjectId = "1"
            }, default);

        // assert
        await Verify(result);
    }

    [Fact]
    public async Task SubjectPermissionWithDepthLimit()
    {
        // Arrange
        var tuples = new RelationTuple[]
        {
            new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "group_members",
                TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers),
        };

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

        var engine = await CreateEngine(tuples, [], schema);

        // Act
        var request = new SubjectPermissionRequest()
        {
            EntityType = TestsConsts.Workspaces.Identifier,
            EntityId = TestsConsts.Workspaces.PublicWorkspace,
            SubjectType = TestsConsts.Users.Identifier,
            SubjectId = TestsConsts.Users.Alice,
            Depth = 1
        };
        var result = await engine.SubjectPermission(request, default);

        // Assert
        await Verify(result);
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
                permission edit:= isActiveStatus(status);
            }
            fn isActiveStatus(status string) => status == ""active"";
        ";
        // act
        var engine = await CreateEngine([], [
            new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "status",
                JsonValue.Create("active")!)
        ], schema);

        // Act
        var result = await engine.Check(new CheckRequest(TestsConsts.Workspaces.Identifier,
            TestsConsts.Workspaces.PublicWorkspace, "edit", "user", "1"), default);

        // assert
        result.Should().BeTrue();
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
                new RelationTuple("account", "1", "owner", TestsConsts.Users.Identifier, "1")
            ],
            [
                new AttributeTuple("account", "1", "balance",
                    JsonValue.Create(872.54m))
            ], schema);

        // Act
        var result = await engine.Check(new CheckRequest("account",
            "1", "withdraw", "user", "1"), default);

        // assert
        result.Should().BeTrue();
    }
    
    [Fact]
    public async Task ReflexiveFastPath_ShouldReturnTrue_WithoutDbCall()
    {
        // Arrange — schema allows group#member as subject of group.member (self-referential)
        var schema = @"
            entity user {}
            entity group {
                relation member @user @group#member;
            }
        ";
        // No tuples needed — reflexive fast-path short-circuits before any DB call
        var engine = await CreateEngine([], [], schema);

        // Act — group:admins#member checking group:admins#member (reflexive)
        var result = await engine.Check(new CheckRequest
        {
            EntityType = "group",
            EntityId = "admins",
            Permission = "member",
            SubjectType = "group",
            SubjectId = "admins",
            SubjectRelation = "member"
        }, default);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task TypeGuard_ShouldReturnFalse_WhenSubjectTypeNotAllowedInRelation()
    {
        // Arrange — workspace.owner only allows @user, not @group
        var schema = @"
            entity user {}
            entity group {
                relation member @user;
            }
            entity workspace {
                relation owner @user;
                permission delete := owner;
            }
        ";
        // No tuples — type guard prunes before any DB call
        var engine = await CreateEngine([], [], schema);

        // Act — subject type "group" is not listed in workspace.owner relation
        var result = await engine.Check(new CheckRequest
        {
            EntityType = "workspace",
            EntityId = "1",
            Permission = "delete",
            SubjectType = "group",
            SubjectId = "admins"
        }, default);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ReachabilityPruning_ShouldNotPrune_AttributeDrivenUnion()
    {
        var schema = @"
            entity user {}
            entity group {}
            entity workspace {
                relation owner @user;
                attribute public bool;
                permission view := owner or public;
            }
        ";

        var engine = await CreateEngine([], [
            new AttributeTuple("workspace", "1", "public", JsonValue.Create(true))
        ], schema);

        var result = await engine.Check(new CheckRequest
        {
            EntityType = "workspace",
            EntityId = "1",
            Permission = "view",
            SubjectType = "group",
            SubjectId = "admins"
        }, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ReachabilityPruning_ShouldReturnFalse_ForImpossibleNestedPath()
    {
        var schema = @"
            entity user {}
            entity group {
                relation member @user;
            }
            entity workspace {
                relation parent @group;
                permission view := parent.member;
            }
        ";

        var engine = await CreateEngine([], [], schema);

        var result = await engine.Check(new CheckRequest
        {
            EntityType = "workspace",
            EntityId = "1",
            Permission = "view",
            SubjectType = "group",
            SubjectId = "admins"
        }, default);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ReachabilityPruning_ShouldRemainConservative_ForCyclicPermission()
    {
        var schema = @"
            entity user {}
            entity group {
                relation member @user @group#member;
                permission access := member;
            }
        ";

        var engine = await CreateEngine([], [], schema);

        var result = await engine.Check(new CheckRequest
        {
            EntityType = "group",
            EntityId = "admins",
            Permission = "access",
            SubjectType = "group",
            SubjectId = "admins",
            SubjectRelation = "member"
        }, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TupleToUserSet_BatchPath_Should_Return_True_For_Homogeneous_DirectRelation()
    {
        var schema = @"
            entity user {}
            entity team {
                relation member @user;
            }
            entity project {
                relation team_link @team;
                permission view := team_link.member;
            }
        ";

        var engine = await CreateEngine([
            new RelationTuple("project", "1", "team_link", "team", "alpha"),
            new RelationTuple("project", "1", "team_link", "team", "beta"),
            new RelationTuple("team", "beta", "member", "user", "alice")
        ], [], schema);

        var result = await engine.Check(new CheckRequest
        {
            EntityType = "project",
            EntityId = "1",
            Permission = "view",
            SubjectType = "user",
            SubjectId = "alice"
        }, default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Check_RbacViaSubRelation_ReturnsTrue()
    {
        // admin @role#assignee — "user" is not a direct entity, reaches via sub-relation path.
        // Regression test: IsSubjectTypeAllowedInRelation was blocking this path early.
        const string rbacSchema = """
            entity user {}
            entity role {
                relation assignee @user;
            }
            entity resource {
                relation admin  @role#assignee;
                relation editor @role#assignee;
                relation viewer @role#assignee;
                permission read := viewer or editor or admin;
            }
            """;
        var engine = await CreateEngine(
            [
                new RelationTuple("role", "admin_role", "assignee", "user", "alice"),
                new RelationTuple("resource", "api", "admin", "role", "admin_role", "assignee"),
            ],
            [], rbacSchema);

        var result = await engine.Check(new CheckRequest
        {
            EntityType = "resource", EntityId = "api",
            Permission = "read",
            SubjectType = "user", SubjectId = "alice"
        }, default);

        result.Should().BeTrue();
    }

    public static TheoryData<string, decimal?, bool> ContextAccessTheoryData = new()
    {
        {"withdraw_amount", 500.0m, true},
        {"withdraw_amount", 1000.0m, false},
        {"withdraw_amount", 872.54m, true},
        {"withdraw_amount", null, false},
        {"amount", 500.0m, false}
    };

    [Theory, MemberData(nameof(ContextAccessTheoryData))]
    public async Task Test_function_call_with_context_arg(string key, decimal? value, bool shouldPass = true)
    {
        var schema = @"
            entity user {}
            entity account {
                relation owner @user;
                attribute balance decimal;
                permission withdraw := owner and check_balance(balance, context.withdraw_amount);
            }
            fn check_balance(balance decimal, amount decimal) => balance >= amount;
        ";
        
        // act
        var engine = await CreateEngine(
            [
                new RelationTuple("account", "1", "owner", TestsConsts.Users.Identifier, "1")
            ],
            [
                new AttributeTuple("account", "1", "balance",
                    JsonValue.Create(872.54m))
            ], schema);

        // Act
        var context = new Dictionary<string, object>
        {
            {key, value}
        };
        var result = await engine.Check(new CheckRequest("account",
            "1", "withdraw", "user", "1", context: context), default);

        // assert
        result.Should().Be(shouldPass);
    }

    [Fact]
    public async Task Check_Not_Relation_Should_Return_False_For_Owner()
    {
        var schema = @"
            entity user {}
            entity document {
                relation owner @user;
                permission non_owner := not(owner);
            }
        ";
        var engine = await CreateEngine(
            [new RelationTuple("document", "doc1", "owner", "user", "alice")],
            [], schema);

        var result = await engine.Check(
            new CheckRequest("document", "doc1", "non_owner", "user", "alice"), default);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Check_Not_Relation_Should_Return_True_For_Non_Owner()
    {
        var schema = @"
            entity user {}
            entity document {
                relation owner @user;
                permission non_owner := not(owner);
            }
        ";
        var engine = await CreateEngine(
            [new RelationTuple("document", "doc1", "owner", "user", "alice")],
            [], schema);

        var result = await engine.Check(
            new CheckRequest("document", "doc1", "non_owner", "user", "bob"), default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Check_Not_Inside_And_Returns_False_When_Subject_Matches_Negate()
    {
        var schema = @"
            entity user {}
            entity document {
                relation owner @user;
                relation viewer @user;
                permission restricted_view := viewer and not(owner);
            }
        ";
        var engine = await CreateEngine(
            [
                new RelationTuple("document", "doc1", "owner", "user", "alice"),
                new RelationTuple("document", "doc1", "viewer", "user", "alice"),
            ],
            [], schema);

        var result = await engine.Check(
            new CheckRequest("document", "doc1", "restricted_view", "user", "alice"), default);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Check_Not_Inside_And_Returns_True_When_Subject_Is_Viewer_But_Not_Owner()
    {
        var schema = @"
            entity user {}
            entity document {
                relation owner @user;
                relation viewer @user;
                permission restricted_view := viewer and not(owner);
            }
        ";
        var engine = await CreateEngine(
            [
                new RelationTuple("document", "doc1", "owner", "user", "alice"),
                new RelationTuple("document", "doc1", "viewer", "user", "bob"),
            ],
            [], schema);

        var result = await engine.Check(
            new CheckRequest("document", "doc1", "restricted_view", "user", "bob"), default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Check_Not_Compound_Expression_Returns_False_When_Subject_Matches_Inner_Union()
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
                new RelationTuple("document", "doc1", "editor", "user", "alice"),
            ],
            [], schema);

        // alice is editor → matches inner union → not(union) = false
        var result = await engine.Check(
            new CheckRequest("document", "doc1", "non_contributor", "user", "alice"), default);

        result.Should().BeFalse();
    }

    /// <summary>
    /// Verifies two-hop permission composition: project.edit := contributor or parent.manage,
    /// where team.manage := owner or parent.admin and parent is an organization.
    /// A user who is org admin should be able to edit the project without any direct project relation.
    /// </summary>
    [Fact]
    public async Task Check_TwoHopNestedPermission_OrgAdminCanEditProject()
    {
        const string schema = """
            entity user {}
            entity organization {
                relation admin @user;
                relation member @user;
            }
            entity team {
                relation parent @organization;
                relation owner @user;
                relation member @user;
                permission manage := owner or parent.admin;
                permission view   := member or owner or parent.admin or parent.member;
            }
            entity project {
                relation parent @team;
                relation contributor @user;
                permission edit := contributor or parent.manage;
                permission view := contributor or parent.view;
            }
            """;

        var engine = await CreateEngine([
            new RelationTuple("organization", "org-1", "admin",  "user", "alice"),
            new RelationTuple("team",         "team-1", "parent", "organization", "org-1"),
            new RelationTuple("project",      "proj-1", "parent", "team", "team-1"),
        ], [], schema);

        // alice is org admin → team.manage is true → project.edit is true
        (await engine.Check(new CheckRequest("project", "proj-1", "edit", "user", "alice"), default))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Check_TwoHopNestedPermission_OrgMemberCanViewProject()
    {
        const string schema = """
            entity user {}
            entity organization {
                relation admin @user;
                relation member @user;
            }
            entity team {
                relation parent @organization;
                relation owner @user;
                relation member @user;
                permission manage := owner or parent.admin;
                permission view   := member or owner or parent.admin or parent.member;
            }
            entity project {
                relation parent @team;
                relation contributor @user;
                permission edit := contributor or parent.manage;
                permission view := contributor or parent.view;
            }
            """;

        var engine = await CreateEngine([
            new RelationTuple("organization", "org-1", "member", "user", "bob"),
            new RelationTuple("team",         "team-1", "parent", "organization", "org-1"),
            new RelationTuple("project",      "proj-1", "parent", "team", "team-1"),
        ], [], schema);

        // bob is org member → team.view is true → project.view is true
        (await engine.Check(new CheckRequest("project", "proj-1", "view", "user", "bob"), default))
            .Should().BeTrue();

        // bob is only an org member, not admin → team.manage is false → project.edit is false
        (await engine.Check(new CheckRequest("project", "proj-1", "edit", "user", "bob"), default))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Check_TwoHopNestedPermission_UnrelatedUserCannotAccess()
    {
        const string schema = """
            entity user {}
            entity organization {
                relation admin @user;
                relation member @user;
            }
            entity team {
                relation parent @organization;
                relation owner @user;
                relation member @user;
                permission manage := owner or parent.admin;
                permission view   := member or owner or parent.admin or parent.member;
            }
            entity project {
                relation parent @team;
                relation contributor @user;
                permission edit := contributor or parent.manage;
                permission view := contributor or parent.view;
            }
            """;

        var engine = await CreateEngine([
            new RelationTuple("organization", "org-1", "admin",  "user", "alice"),
            new RelationTuple("team",         "team-1", "parent", "organization", "org-1"),
            new RelationTuple("project",      "proj-1", "parent", "team", "team-1"),
        ], [], schema);

        // charlie has no relation to anything → both false
        (await engine.Check(new CheckRequest("project", "proj-1", "edit", "user", "charlie"), default))
            .Should().BeFalse();
        (await engine.Check(new CheckRequest("project", "proj-1", "view", "user", "charlie"), default))
            .Should().BeFalse();
    }

    /// <summary>
    /// Verifies a self-referential permission cascades through a chain of same-type parents:
    /// organisation.manage_users := user_manager or parent.manage_users, where parent is an
    /// organisation. A user who is a user_manager at the root org should have manage_users at
    /// every descendant org via the recursive parent.manage_users, while an unrelated user does not.
    /// </summary>
    [Fact]
    public async Task Check_SelfReferentialPermission_CascadesThroughParentChain()
    {
        const string schema = """
            entity user {}
            entity organisation {
                relation parent       @organisation;
                relation user_manager @user;
                permission manage_users := user_manager or parent.manage_users;
            }
            """;

        // org-root <- org-child <- org-grandchild ; alice is user_manager only at org-root
        var engine = await CreateEngine([
            new RelationTuple("organisation", "org-root",       "user_manager", "user",         "alice"),
            new RelationTuple("organisation", "org-child",      "parent",       "organisation", "org-root"),
            new RelationTuple("organisation", "org-grandchild", "parent",       "organisation", "org-child"),
        ], [], schema);

        // direct: alice is user_manager at root
        (await engine.Check(new CheckRequest("organisation", "org-root", "manage_users", "user", "alice"), default))
            .Should().BeTrue();

        // one hop up the parent chain
        (await engine.Check(new CheckRequest("organisation", "org-child", "manage_users", "user", "alice"), default))
            .Should().BeTrue();

        // two hops up the parent chain (the cascading case)
        (await engine.Check(new CheckRequest("organisation", "org-grandchild", "manage_users", "user", "alice"), default))
            .Should().BeTrue();

        // negative: bob has no relation anywhere → false at every level
        (await engine.Check(new CheckRequest("organisation", "org-grandchild", "manage_users", "user", "bob"), default))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Check_Not_Compound_Expression_Returns_True_When_Subject_Matches_Nothing_Inside()
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
                new RelationTuple("document", "doc1", "editor", "user", "bob"),
            ],
            [], schema);

        // charlie has neither relation → not(owner or editor) = true
        var result = await engine.Check(
            new CheckRequest("document", "doc1", "non_contributor", "user", "charlie"), default);

        result.Should().BeTrue();
    }
}
