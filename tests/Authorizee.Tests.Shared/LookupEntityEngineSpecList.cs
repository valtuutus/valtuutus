using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Authorizee.Core;

namespace Authorizee.Tests.Shared;

public static class LookupEntityEngineSpecList
{
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        TopLevelChecks => new()
    {
        {
            // Checks direct relation
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Groups.Admins, TestsConsts.Groups.Designers, TestsConsts.Groups.Developers
            ])
        },
        {
            // Checks direct relation, but alice is not a part of the group
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Bob)
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([])
        },
        {
            // Checks attribute
            [
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([])
        },
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        IndirectRelationLookup => new()
    {
        {
            // Checks indirect relation
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsMaisBrabos, "member",
                    TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member"),
                
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsBrabos, "member",
                    TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member")
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Teams.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Teams.OsMaisBrabos,
                TestsConsts.Teams.OsBrabos,
            ])
        },
        {
            // Checks indirect and direct relations
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsMaisBrabos, "member",
                    TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member"),
                
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsBrabos, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Teams.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Teams.OsMaisBrabos,
                TestsConsts.Teams.OsBrabos,
            ])
        },
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        SimplePermissionLookup => new()
    {
        {
            // Checks indirect relation
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "delete", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Workspaces.PublicWorkspace,
                TestsConsts.Workspaces.PrivateWorkspace,
            ])
        },
        {
            // Checks indirect relation
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "delete", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Workspaces.PrivateWorkspace,
            ])
        },
    };
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        IntersectWithRelationAndAttributePermissionLookup => new()
    {
        {
            // with public attribute
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            ],
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Workspaces.PublicWorkspace,
            ])
        },
        {
            // without public attribute
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([])
        },
        {
            // without public attribute as false
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ],
            [
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(false))
            ],
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([])
        },
    };
}