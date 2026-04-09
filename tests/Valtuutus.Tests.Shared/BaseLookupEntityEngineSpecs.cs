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
}
