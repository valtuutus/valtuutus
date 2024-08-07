using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Schemas;
using FluentAssertions;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Tests.Shared;

public abstract class BaseCheckEngineSpecs
{
    protected abstract ValueTask<CheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        Schema? schema = null);

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
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("project")
            .WithRelation("member", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("admin", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithPermission("view", PermissionNode.Union("member", "admin"))
            .SchemaBuilder.Build();
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
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("project")
            .WithRelation("owner", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("whatever", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithPermission("delete", PermissionNode.Intersect("owner", "whatever"))
            .SchemaBuilder.Build();
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
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("project")
            .WithRelation("member", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("admin", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithAttribute("public", typeof(bool))
            .WithPermission("view", PermissionNode.Union("member", "public"))
            .SchemaBuilder.Build();
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
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("project")
            .WithRelation("member", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithAttribute("public", typeof(bool))
            .WithPermission("comment", PermissionNode.Intersect("public", "member"))
            .SchemaBuilder.Build();
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
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier)
            .WithRelation("admin", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("member", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithEntity("project")
            .WithRelation("parent", rc => rc.WithEntityType(TestsConsts.Workspaces.Identifier))
            .WithPermission("delete", PermissionNode.Leaf("parent.admin"))
            .SchemaBuilder.Build();
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
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier)
            .WithRelation("admin", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("member", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithEntity("project")
            .WithRelation("admin", rc => rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("parent", rc => rc.WithEntityType(TestsConsts.Workspaces.Identifier))
            .WithPermission("delete", PermissionNode.Union("parent.admin", "admin"))
            .SchemaBuilder.Build();
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
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier)
            .WithRelation("admin", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("member", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithEntity("project")
            .WithRelation("admin", rc => rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("parent", rc => rc.WithEntityType(TestsConsts.Workspaces.Identifier))
            .WithPermission("delete", PermissionNode.Intersect("parent.admin", "admin"))
            .SchemaBuilder.Build();
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
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier)
            .WithRelation("admin", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("member", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithPermission("view", PermissionNode.Union("admin", "member"))
            .WithEntity("project")
            .WithRelation("admin", rc => rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("parent", rc => rc.WithEntityType(TestsConsts.Workspaces.Identifier))
            .WithPermission("view", PermissionNode.Leaf("parent.view"))
            .SchemaBuilder.Build();
        var engine = await CreateEngine(tuples, attributes, schema);

        // Act
        var result = await engine.Check(request, default);

        // assert
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
        var result = await engine.Check(new CheckRequest
        {
            EntityType = "workspace",
            Permission = "view",
            EntityId = "1",
            SubjectId = "1",
            SubjectType = "user"
        }, default);


        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SubjectPermissionsWhenNoPermissionsShouldReturnEmpty()
    {
        // Arrange
        var schema = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier)
            .WithRelation("admin", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("member", rc =>
                rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithEntity("project")
            .WithRelation("admin", rc => rc.WithEntityType(TestsConsts.Users.Identifier))
            .WithRelation("parent", rc => rc.WithEntityType(TestsConsts.Workspaces.Identifier))
            .WithPermission("view", PermissionNode.Leaf("parent.view"))
            .SchemaBuilder.Build();
        var engine = await CreateEngine([], [], schema);


        // Act
        var result = await engine.SubjectPermission(new SubjectPermissionRequest
        {
            EntityType = "workspace",
            EntityId = "1",
            SubjectType = "user",
            SubjectId = "1"
        }, default);


        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SubjectPermissionShouldListAllPermissions()
    {
        // arrange
        var entity = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier).WithAttribute("public", typeof(bool));

        for (int i = 0; i < 50; i++)
        {
            entity.WithPermission($"permission_{i}", PermissionNode.Leaf("public"));
        }

        var schema = entity.SchemaBuilder.Build();

        // act
        var engine = await CreateEngine([], [], schema);

        // Act
        var result = await engine.SubjectPermission(new SubjectPermissionRequest
        {
            EntityType = "workspace",
            EntityId = "1",
            SubjectType = "user",
            SubjectId = "1"
        }, default);

        // assert
        await Verifier.Verify(result);
    }


    [Fact]
    public async Task SubjectPermissionShouldEvaluatePermissions()
    {
        // arrange
        var entity = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier).WithAttribute("public", typeof(bool));

        for (int i = 0; i < 50; i++)
        {
            entity.WithPermission($"permission_{i}", PermissionNode.Leaf("public"));
        }

        var schema = entity.SchemaBuilder.Build();

        // act
        var engine = await CreateEngine([], [
            new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public",
                JsonValue.Create(true))
        ], schema);

        // Act
        var result = await engine.SubjectPermission(new SubjectPermissionRequest
        {
            EntityType = TestsConsts.Workspaces.Identifier,
            EntityId = TestsConsts.Workspaces.PublicWorkspace,
            SubjectType = "user",
            SubjectId = "1"
        }, default);

        // assert
        await Verifier.Verify(result);
    }

    [Fact]
    public async Task TestStringBasedAttributeExpression()
    {
        // arrange
        var entity = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity(TestsConsts.Workspaces.Identifier)
            .WithAttribute("status", typeof(string))
            .WithPermission("edit", PermissionNode.AttributeStringExpression("status", s => s == "active"));

        var schema = entity.SchemaBuilder.Build();

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
        var entity = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("account")
            .WithRelation("owner", c => c.WithEntityType(TestsConsts.Users.Identifier))
            .WithAttribute("balance", typeof(decimal))
            .WithPermission("withdraw", PermissionNode.Intersect(
                PermissionNode.Leaf("owner"),
                PermissionNode.AttributeDecimalExpression("balance", b => b >= 500m)
            ));

        var schema = entity.SchemaBuilder.Build();

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
}