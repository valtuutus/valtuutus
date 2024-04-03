using System.Text.Json.Nodes;
using Valtuutus.Core;

namespace Valtuutus.Tests.Shared;

public static class LookupSubjectEngineSpecList
{
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        TopLevelChecks => new()
    {
        {
            // Checks direct relation
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Bob),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Charlie)
            ],
            [
            ],
            new LookupSubjectRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Groups.Admins),
            new HashSet<string>([
                TestsConsts.Users.Alice, TestsConsts.Users.Bob, TestsConsts.Users.Charlie,
            ])
        },
        {
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Bob),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier,
                    TestsConsts.Users.Charlie)
            ],
            [
            ],
            new LookupSubjectRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Groups.Admins),
            new HashSet<string>([
                TestsConsts.Users.Alice, TestsConsts.Users.Bob
            ])
        }
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        IndirectRelationLookup => new()
    {
        {
            // Checks indirect relation
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie),

                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsMaisBrabos, "member",
                    TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member")
            ],
            [
            ],
            new LookupSubjectRequest(TestsConsts.Teams.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Teams.OsMaisBrabos),
            new HashSet<string>([
                TestsConsts.Users.Alice, TestsConsts.Users.Bob, TestsConsts.Users.Charlie,
            ])
        },
        {
            // Checks indirect and direct relation
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie),

                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsMaisBrabos, "member",
                    TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member"),

                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsMaisBrabos, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Dan)
            ],
            [
            ],
            new LookupSubjectRequest(TestsConsts.Teams.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Teams.OsMaisBrabos),
            new HashSet<string>([
                TestsConsts.Users.Alice, TestsConsts.Users.Bob, TestsConsts.Users.Charlie, TestsConsts.Users.Dan
            ])
        },
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        SimplePermissionLookup => new()
    {
        {
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            ],
            [
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "delete", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PrivateWorkspace),
            new HashSet<string>([
                TestsConsts.Users.Alice,
                TestsConsts.Users.Bob,
            ])
        },
        {
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            ],
            [
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "delete", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PrivateWorkspace),
            new HashSet<string>([
                TestsConsts.Users.Alice,
            ])
        }
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        IntersectWithRelationAndAttributePermissionLookup => new()
    {
        {
            // with public attribute
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie)
            ],
            [
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public",
                    JsonValue.Create(true))
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PublicWorkspace),
            new HashSet<string>([
                TestsConsts.Users.Alice, TestsConsts.Users.Bob, TestsConsts.Users.Charlie
            ])
        },
        {
            // with public attribute
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie)
            ],
            [
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public",
                    JsonValue.Create(false))
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PublicWorkspace),
            new HashSet<string>([
            ])
        },
        {
            // with public attribute
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie)
            ],
            [
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PublicWorkspace),
            new HashSet<string>([
            ])
        },
        {
            // with public attribute
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie)
            ],
            [
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public",
                    JsonValue.Create(true))
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PrivateWorkspace),
            new HashSet<string>([
            ])
        },
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, HashSet<string>>
        IntersectAttributeExpWithOtherNodes => new()
    {
        {
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace),
            ],
            [
                new AttributeTuple("project", "1", "status", JsonValue.Create(1))
            ],
            new LookupSubjectRequest("project", "edit", TestsConsts.Users.Identifier,
                "1"),
            [TestsConsts.Users.Alice, TestsConsts.Users.Bob, TestsConsts.Users.Charlie]
        },
        {
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace),
            ],
            [
                new AttributeTuple("project", "1", "status", JsonValue.Create(2))
            ],
            new LookupSubjectRequest("project", "edit", TestsConsts.Users.Identifier,
                "1"),
            []
        },
        {
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie),
                new("project", "1", "parent", TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace),
            ],
            [
            ],
            new LookupSubjectRequest("project", "edit", TestsConsts.Users.Identifier,
                "1"),
            []
        },
    };
}