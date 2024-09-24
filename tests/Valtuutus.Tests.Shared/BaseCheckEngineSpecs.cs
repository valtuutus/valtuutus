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
}