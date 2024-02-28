using System.Text.Json.Nodes;
using Authorizee.Core;
using Xunit;

namespace Authorizee.Tests.Shared;

public static class CheckEngineSpecList
{
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> TopLevelChecks => new()
    {

        {
            // Checks direct relation
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new CheckRequest(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks direct relation, but alice is not a part of the group
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new CheckRequest(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks attribute
            [
            ],
            [
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            ],
            new CheckRequest(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public"),
            true
        },
        {
            // Checks attribute, but should fail
            [
            ],
            [
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "public", JsonValue.Create(false))
            ],
            new CheckRequest(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "public"),
            false
        },
        {
            // Checks permission top level
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new CheckRequest(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "delete", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks permission but should fail
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new CheckRequest(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "delete", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        }
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionRelationsData => new()
    {
        {
            // Checks union of two relations, both true
            [
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of two relations, first is false
            [
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of two relations, second is false
            [
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of two relations, both are false
            [
            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> IntersectionRelationsData => new()
    {
        {
            // Checks intersection of two relations, both true
            [
                new("project", "1", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "whatever", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersection of two relations, first is false
            [
                new("project", "1", "whatever", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersection of two relations, second is false
            [
                new("project", "1", "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersection of two permissions, both are false
            [
            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionRelationsAttributesData => new()
    {

        {
            // Checks union of attr and rel, both true
            [
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
                new AttributeTuple("project", "1", "public", JsonValue.Create(true))
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of attr and rel, first is true
            [
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        
        {
            // Checks union of attr and rel, second is true
            [
            ],
            [
                new AttributeTuple("project", "1", "public", JsonValue.Create(true))

            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of attr and rel, both are false (attr setted)
            [
            ],
            [
                new AttributeTuple("project", "1", "public", JsonValue.Create(false))

            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks union of attr and rel, both are false (attr setted)
            [
            ],
            [

            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },


        
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> IntersectionRelationsAttributesData => new()
    {
        {
            // Checks intersection of attr and rel, both true
            [
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
                new AttributeTuple("project", "1", "public", JsonValue.Create(true))
            ],
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersection of attr and rel, first is true
            [
                new("project", "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        
        {
            // Checks intersection of attr and rel, second is true
            [
            ],
            [
                new AttributeTuple("project", "1", "public", JsonValue.Create(true))

            ],
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersection of attr and rel, both are false (attr setted)
            [
            ],
            [
                new AttributeTuple("project", "1", "public", JsonValue.Create(false))

            ],
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersection of attr and rel, both are false (attr setted)
            [
            ],
            [

            ],
            new CheckRequest("project", "1", "comment",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> NestedRelationData => new()
    {

        {
            // Checks nested relation, true
            [
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1")

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks nested relation, but workspace is not parent of the project
            [
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks nested relation, no relation
            [

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> UnionOfDirectAndNestedRelationData => new()
    {

        {
            // Checks union of relations, both are true
            [
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1")

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of relations, first is false
            [
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of relations, second is false
            [
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks union of relations, both are false
            [

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> IntersectionOfDirectAndNestedRelationData => new()
    {

        {
            // Checks intersect of relations, both are true
            [
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1")

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersect of relations, first is false
            [
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersect of relations, second is false
            [
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },
        {
            // Checks intersect of relations, both are false
            [

            ],
            [
            ],
            new CheckRequest("project", "1", "delete",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], CheckRequest, bool> NestedPermissionsData => new()
    {

        {
            // Checks nested permission, admin
            [
                new(TestsConsts.Workspaces.Identifier, "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1")

            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersect of relations, member
            [
                new(TestsConsts.Workspaces.Identifier, "1", "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "admin", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, "1")

            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            true
        },
        {
            // Checks intersect of relations, no relations
            [

            ],
            [
            ],
            new CheckRequest("project", "1", "view",  TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            false
        },

        
    };
}