using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Engines.LookupEntity;

namespace Valtuutus.Tests.Shared;

public static class LookupEntityEngineSpecList
{
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
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
            new List<string> {
                TestsConsts.Groups.Admins, TestsConsts.Groups.Designers, TestsConsts.Groups.Developers
            }
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
            new List<string>()
        },
        {
            // Checks attribute
            [
            ],
            [
            ],
            new LookupEntityRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new List<string>()
        },
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
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
            new List<string> {
                TestsConsts.Teams.OsMaisBrabos,
                TestsConsts.Teams.OsBrabos,
            }
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
            new List<string> {
                TestsConsts.Teams.OsMaisBrabos,
                TestsConsts.Teams.OsBrabos,
            }
        },
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
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
            new List<string> {
                TestsConsts.Workspaces.PublicWorkspace,
                TestsConsts.Workspaces.PrivateWorkspace,
            }
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
            new List<string> {
                TestsConsts.Workspaces.PrivateWorkspace,
            }
        },
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
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
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public",
                    JsonValue.Create(true))
            ],
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new List<string> {
                TestsConsts.Workspaces.PublicWorkspace,
            }
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
            new List<string>()
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
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public",
                    JsonValue.Create(false))
            ],
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new List<string>()
        },
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        IntersectAttributeExpressionWithOtherNodes = new()
        {
            {
                [
                    new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                        TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                    new("project", "1", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                    new("project", "2", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                    new("project", "3", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                ],
                [
                    new("project", "1", "status", JsonValue.Create(1)),
                    new("project", "2", "status", JsonValue.Create(1)),
                    new("project", "3", "status", JsonValue.Create(1)),
                    new("project", "4", "status", JsonValue.Create(2)),
                ],
                new LookupEntityRequest("project", "edit", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new List<string> { "1", "2", "3" }
            },
            {
                [
                    new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                        TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                    new("project", "1", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                    new("project", "2", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                    new("project", "3", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                ],
                [
                    new("project", "1", "status", JsonValue.Create(2)),
                    new("project", "2", "status", JsonValue.Create(2)),
                    new("project", "3", "status", JsonValue.Create(2)),
                    new("project", "4", "status", JsonValue.Create(2)),
                ],
                new LookupEntityRequest("project", "edit", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new List<string>()
            },
            {
                [
                    new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "admin",
                        TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                    new("project", "1", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                    new("project", "2", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                    new("project", "3", "parent", TestsConsts.Workspaces.Identifier,
                        TestsConsts.Workspaces.PrivateWorkspace),
                ],
                [
                ],
                new LookupEntityRequest("project", "edit", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new List<string>()
            },
        };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>> UnionRelationDepthLimit => new()
    {
        {
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "group_members", TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers),
            ],
            [],
            new LookupEntityRequest()
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                Permission = "view",
                SubjectType = TestsConsts.Users.Identifier,
                SubjectId = TestsConsts.Users.Alice,
                Depth = 1
            },
            new List<string>()
        },
        {
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "group_members", TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers),
            ],
            [],
            new LookupEntityRequest()
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                Permission = "view",
                SubjectType = TestsConsts.Users.Identifier,
                SubjectId = TestsConsts.Users.Alice,
                Depth = 2
            },
            new List<string> { TestsConsts.Workspaces.PublicWorkspace }
        },
        {
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member", TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "group_members", TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers),
            ],
            [],
            new LookupEntityRequest()
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                Permission = "view",
                SubjectType = TestsConsts.Users.Identifier,
                SubjectId = TestsConsts.Users.Alice,
            },
            new List<string> { TestsConsts.Workspaces.PublicWorkspace }
        }
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        ScopedLookup => new()
    {
        {
            // User can view all tasks in project 1 (alice is a member of proj1 and proj2)
            [
                new("project", "proj1", "member", "user", "alice"),
                new("project", "proj2", "member", "user", "alice"),
                new("task", "task1", "parent", "project", "proj1"),
                new("task", "task2", "parent", "project", "proj1"),
                new("task", "task3", "parent", "project", "proj2"),
            ],
            [],
            new LookupEntityRequest("task", "view", "user", "alice")
            {
                Scope = new EntityScope("parent", "project", "proj1")
            },
            new List<string> { "task1", "task2" }
        },
        {
            // User has no access — scope returns empty even though tasks exist
            [
                new("project", "proj1", "member", "user", "bob"),
                new("task", "task1", "parent", "project", "proj1"),
            ],
            [],
            new LookupEntityRequest("task", "view", "user", "alice")
            {
                Scope = new EntityScope("parent", "project", "proj1")
            },
            new List<string>()
        },
        {
            // Scope subject does not exist (no tuples match JOIN) — returns empty
            [
                new("project", "proj1", "member", "user", "alice"),
                new("task", "task1", "parent", "project", "proj1"),
            ],
            [],
            new LookupEntityRequest("task", "view", "user", "alice")
            {
                Scope = new EntityScope("parent", "project", "nonexistent-project")
            },
            new List<string>()
        },
    };

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, IReadOnlyList<string>>
        PaginatedLookup => new()
    {
        {
            // No scope, no pagination — all results, null token
            [
                new("project", "proj1", "member", "user", "alice"),
                new("task", "task1", "parent", "project", "proj1"),
                new("task", "task2", "parent", "project", "proj1"),
            ],
            [],
            new LookupEntityRequest("task", "view", "user", "alice"),
            new List<string> { "task1", "task2" }
        },
    };
}
