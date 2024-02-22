using System.Text.Json.Nodes;
using Authorizee.Core;
using Authorizee.Core.Schemas;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Tests;

public sealed class CheckEngineSpecs
{
    
    public static CheckEngine CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        var logger = Substitute.For<ILogger<CheckEngine>>();
        var schemaGraph = new SchemaGraph( schema ?? TestsConsts.Schemas.schema);
        return new CheckEngine(relationTupleReader, attributeReader, schemaGraph, logger);
    }


    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> TopLevelChecks => new()
    {

        {
            // Checks direct relation
            new RelationTuple[]
            {
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks direct relation, but alice is not a part of the group
            new RelationTuple[]
            {
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks attribute
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            },
            new CheckRequest(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public"),
            true
        },
        {
            // Checks attribute, but should fail
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "public", JsonValue.Create(false))
            },
            new CheckRequest(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "public"),
            false
        },
        {
            // Checks permission top level
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "delete", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks permission but should fail
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "delete", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        }
    };
    
    
    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);
        
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionRelationsData => new()
    {
        {
            // Checks union of two relations, both true
            new RelationTuple[]
            {
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of two relations, first is false
            new RelationTuple[]
            {
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of two relations, second is false
            new RelationTuple[]
            {
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of two relations, both are false
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    
    [Theory]
    [MemberData(nameof(UnionRelationsData))]
    public async Task CheckingSimpleUnionOfRelationsShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var (schema, _) = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("project")
                .WithRelation("member", rc =>
                    rc.WithEntityType(TestsConsts.Users.Identifier))
                .WithRelation("admin", rc =>
                    rc.WithEntityType(TestsConsts.Users.Identifier))
                .WithPermission("view", PermissionNode.Union("member", "admin"))
            .SchemaBuilder.Build();
        var engine = CreateEngine(tuples, attributes, schema);
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> IntersectionRelationsData => new()
    {
        {
            // Checks intersection of two relations, both true
            new RelationTuple[]
            {
                new("project", "1", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "whatever", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersection of two relations, first is false
            new RelationTuple[]
            {
                new("project", "1", "whatever", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersection of two relations, second is false
            new RelationTuple[]
            {
                new("project", "1", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersection of two permissions, both are false
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    
    [Theory]
    [MemberData(nameof(IntersectionRelationsData))]
    public async Task CheckingSimpleIntersectionOfRelationsShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var (schema, _) = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("project")
                .WithRelation("owner", rc =>
                    rc.WithEntityType(TestsConsts.Users.Identifier))
                .WithRelation("whatever", rc =>
                    rc.WithEntityType(TestsConsts.Users.Identifier))
                .WithPermission("delete", PermissionNode.Intersect("owner", "whatever"))
            .SchemaBuilder.Build();
        var engine = CreateEngine(tuples, attributes, schema);
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionRelationsAttributesData => new()
    {

        {
            // Checks union of attr and rel, both true
            new RelationTuple[]
            {
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
                new AttributeTuple("project", "1", "public", JsonValue.Create(true))
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of attr and rel, first is true
            new RelationTuple[]
            {
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        
        {
            // Checks union of attr and rel, second is true
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new AttributeTuple("project", "1", "public", JsonValue.Create(true))

            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of attr and rel, both are false (attr setted)
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new AttributeTuple("project", "1", "public", JsonValue.Create(false))

            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks union of attr and rel, both are false (attr setted)
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {

            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },


        
    };
    
    
    [Theory]
    [MemberData(nameof(UnionRelationsAttributesData))]
    public async Task CheckingSimpleUnionOfRelationsAndAttributesShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var (schema, _) = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("project")
                .WithRelation("member", rc =>
                    rc.WithEntityType(TestsConsts.Users.Identifier))
                .WithRelation("admin", rc =>
                    rc.WithEntityType(TestsConsts.Users.Identifier))
                .WithAttribute("public", typeof(bool))
                .WithPermission("view", PermissionNode.Union("member", "public"))
            .SchemaBuilder.Build();
        var engine = CreateEngine(tuples, attributes, schema);
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> IntersectionRelationsAttributesData => new()
    {
        {
            // Checks intersection of attr and rel, both true
            new RelationTuple[]
            {
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
                new AttributeTuple("project", "1", "public", JsonValue.Create(true))
            },
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersection of attr and rel, first is true
            new RelationTuple[]
            {
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        
        {
            // Checks intersection of attr and rel, second is true
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new AttributeTuple("project", "1", "public", JsonValue.Create(true))

            },
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersection of attr and rel, both are false (attr setted)
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new AttributeTuple("project", "1", "public", JsonValue.Create(false))

            },
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersection of attr and rel, both are false (attr setted)
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {

            },
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
    };
    
    [Theory]
    [MemberData(nameof(IntersectionRelationsAttributesData))]
    public async Task CheckingSimpleIntersectionOfRelationsAndAttributesShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var (schema, _) = new SchemaBuilder()
            .WithEntity(TestsConsts.Users.Identifier)
            .WithEntity("project")
                .WithRelation("member", rc =>
                    rc.WithEntityType(TestsConsts.Users.Identifier))
                .WithAttribute("public", typeof(bool))
                .WithPermission("comment", PermissionNode.Intersect("public", "member"))
            .SchemaBuilder.Build();
        var engine = CreateEngine(tuples, attributes, schema);
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> NestedRelationData => new()
    {

        {
            // Checks nested relation, true
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1"),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks nested relation, but workspace is not parent of the project
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks nested relation, no relation
            new RelationTuple[]
            {

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    
    [Theory]
    [MemberData(nameof(NestedRelationData))]
    public async Task CheckingSimpleNestedRelationShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var (schema, _) = new SchemaBuilder()
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
        var engine = CreateEngine(tuples, attributes, schema);
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionOfDirectAndNestedRelationData => new()
    {

        {
            // Checks union of relations, both are true
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1"),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of relations, first is false
            new RelationTuple[]
            {
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of relations, second is false
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of relations, both are false
            new RelationTuple[]
            {

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    
    [Theory]
    [MemberData(nameof(UnionOfDirectAndNestedRelationData))]
    public async Task CheckingUnionOfDirectAndNestedRelationsShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var (schema, _) = new SchemaBuilder()
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
        var engine = CreateEngine(tuples, attributes, schema);
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> IntersectionOfDirectAndNestedRelationData => new()
    {

        {
            // Checks intersect of relations, both are true
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1"),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersect of relations, first is false
            new RelationTuple[]
            {
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersect of relations, second is false
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersect of relations, both are false
            new RelationTuple[]
            {

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    
    [Theory]
    [MemberData(nameof(IntersectionOfDirectAndNestedRelationData))]
    public async Task CheckingIntersectionOfDirectAndNestedRelationsShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var (schema, _) = new SchemaBuilder()
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
        var engine = CreateEngine(tuples, attributes, schema);
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> NestedPermissionsData => new()
    {

        {
            // Checks nested permission, admin
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1"),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersect of relations, member
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1"),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersect of relations, no relations
            new RelationTuple[]
            {

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    
    [Theory]
    [MemberData(nameof(NestedPermissionsData))]
    public async Task CheckingNestedPermissionsShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var (schema, _) = new SchemaBuilder()
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
        var engine = CreateEngine(tuples, attributes, schema);
        
        // Act
        var result = await engine.Check(request, default);
        
        // assert
        result.Should().Be(expected);
    }
    
    
    
    [Fact]
    public async Task EmptyDataShouldReturnFalseOnPermissions()
    {
        // Arrange
        var engine = CreateEngine([], []);
        
        
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
}