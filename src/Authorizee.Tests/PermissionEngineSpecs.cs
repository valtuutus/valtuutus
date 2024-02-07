using System.Text.Json.Nodes;
using Authorizee.Core;
using Authorizee.Core.Schemas;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Tests;

public sealed class PermissionEngineSpecs
{
    private static readonly (Schema schema, SchemaGraph schemaGraph) Schemas =  new SchemaBuilder()
            .WithEntity("user")
            .WithEntity("group")
                .WithRelation("member", rc =>
                    rc.WithEntityType("user")
                )
            .WithEntity("workspace")
                .WithRelation("owner", rc =>
                    rc.WithEntityType("user"))
                .WithRelation("admin", rc =>
                    rc.WithEntityType("user"))
                .WithRelation("member", rc =>
                    rc.WithEntityType("user"))
                .WithAttribute("public", typeof(bool))
                .WithPermission("comment", PermissionNode.Intersect("member", PermissionNode.Leaf("public")))
                .WithPermission("delete", PermissionNode.Leaf("owner"))
                .WithPermission("view", PermissionNode.Union(
                    PermissionNode.Leaf("public"), PermissionNode.Leaf("owner"), 
                    PermissionNode.Leaf("member"), PermissionNode.Leaf("admin"))
                )
            .WithEntity("team")
                .WithRelation("lead", rc => rc.WithEntityType("user"))
                .WithRelation("member", rc =>
                    rc.WithEntityType("user")
                        .WithEntityType("group", "member"))
            .WithEntity("project")
                .WithRelation("parent", rc => rc.WithEntityType("workspace"))
                .WithRelation("team", rc => rc.WithEntityType("team"))
                .WithRelation("member", rc =>
                    rc.WithEntityType("user")
                        .WithEntityType("team", "member"))
                .WithRelation("lead", rc => rc.WithEntityType("team", "lead"))
                .WithAttribute("public", typeof(bool))
                .WithPermission("view", PermissionNode.Union(
                    PermissionNode.Leaf("member"), PermissionNode.Leaf("lead"), PermissionNode.Intersect("public", "parent.view"))
                )
            .WithEntity("task")
                .WithRelation("parent", rc => rc.WithEntityType("project"))
                .WithRelation("assignee", rc =>
                    rc.WithEntityType("user")
                        .WithEntityType("group", "member"))
                .WithPermission("view", PermissionNode.Leaf("parent.view")).SchemaBuilder.Build();


    private static class Users
    {
        public const string Identifier = "user";
        public const string Alice = "alice";
        public const string Bob = "bob";
        public const string Charlie = "charlie";
        public const string Dan = "dan";
        public const string Eve = "eve";
    }
    
    private static class Groups
    {
        public const string Identifier = "group";
        public const string Admins = "admins";
        public const string Developers = "developers";
        public const string Designers = "designers";
    }
    
    private static class Workspaces
    {
        public const string Identifier = "workspace";
        public const string PublicWorkspace = "1";
        public const string PrivateWorkspace = "2";
    }
    
    
    public static PermissionEngine CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        var logger = Substitute.For<ILogger<PermissionEngine>>();
        return new PermissionEngine(relationTupleReader, attributeReader, Schemas.schema, logger);
    }


    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> TopLevelChecks => new()
    {

        {
            // Checks direct relation
            new RelationTuple[]
            {
                new(Groups.Identifier, Groups.Admins, "member", Users.Identifier, Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(Groups.Identifier, Groups.Admins, "member",  Users.Identifier, Users.Alice),
            true
        },
        {
            // Checks direct relation, but alice is not a part of the group
            new RelationTuple[]
            {
                new(Groups.Identifier, Groups.Designers, "member", Users.Identifier, Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(Groups.Identifier, Groups.Admins, "member",  Users.Identifier, Users.Alice),
            false
        },
        {
            // Checks attribute
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new AttributeTuple(Workspaces.Identifier, Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            },
            new CheckRequest(Workspaces.Identifier, Workspaces.PublicWorkspace, "public"),
            true
        },
        {
            // Checks attribute, but should fail
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new AttributeTuple(Workspaces.Identifier, Workspaces.PrivateWorkspace, "public", JsonValue.Create(false))
            },
            new CheckRequest(Workspaces.Identifier, Workspaces.PrivateWorkspace, "public"),
            false
        },
        {
            // Checks permission top level
            new RelationTuple[]
            {
                new(Workspaces.Identifier, Workspaces.PublicWorkspace, "owner", Users.Identifier, Users.Alice)
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(Workspaces.Identifier, Workspaces.PublicWorkspace, "delete", Users.Identifier, Users.Alice),
            true
        },
        {
            // Checks permission but should fail
            new RelationTuple[]
            {
                new(Workspaces.Identifier, Workspaces.PrivateWorkspace, "owner", Users.Identifier, Users.Alice)
            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(Workspaces.Identifier, Workspaces.PublicWorkspace, "delete", Users.Identifier, Users.Alice),
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
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> DirectRelationPermissions => new()
    {

        {
            // Checks intersect of two permissions
            new RelationTuple[]
            {
                new(Workspaces.Identifier, Workspaces.PublicWorkspace, "member", Users.Identifier, Users.Alice),
            },
            new AttributeTuple[]
            {
                new(Workspaces.Identifier, Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            },
            new CheckRequest(Workspaces.Identifier, Workspaces.PublicWorkspace, "comment",  Users.Identifier, Users.Alice),
            true
        },
        {
            // Checks intersect of two permissions, but the first one is false
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
                new(Workspaces.Identifier, Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            },
            new CheckRequest(Workspaces.Identifier, Workspaces.PublicWorkspace, "comment",  Users.Identifier, Users.Alice),
            false
        },
        {
            // Checks intersect of two permissions, but the second one is false
            new RelationTuple[]
            {
                new(Workspaces.Identifier, Workspaces.PublicWorkspace, "member", Users.Identifier, Users.Alice),

            },
            new AttributeTuple[]
            {
            },
            new CheckRequest(Workspaces.Identifier, Workspaces.PublicWorkspace, "comment",  Users.Identifier, Users.Alice),
            false
        }
    };
    
    
    [Theory]
    [MemberData(nameof(DirectRelationPermissions))]
    public async Task DirectPermissionChecksShouldReturnExpected(RelationTuple[] tuples, AttributeTuple[] attributes, CheckRequest request, bool expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);
        
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