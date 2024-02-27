using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Authorizee.Core;
using Authorizee.Core.Schemas;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Tests;

public class LookupSubjectEngineSpecs
{
    public static LookupSubjectEngine CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        Schema? schema = null)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        return new LookupSubjectEngine(schema ?? TestsConsts.Schemas, relationTupleReader, attributeReader);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
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
            new ConcurrentBag<string>([
                TestsConsts.Users.Alice, TestsConsts.Users.Bob, TestsConsts.Users.Charlie,
            ])
        },
        {
            [
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Bob),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Charlie)
            ],
            [
            ],
            new LookupSubjectRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Groups.Admins),
            new ConcurrentBag<string>([
                TestsConsts.Users.Alice, TestsConsts.Users.Bob
            ])
        }
    };
    
    
    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupSubjectRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
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
            new ConcurrentBag<string>([
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
            new ConcurrentBag<string>([
                TestsConsts.Users.Alice, TestsConsts.Users.Bob, TestsConsts.Users.Charlie, TestsConsts.Users.Dan
            ])
        },
    };

    [Theory]
    [MemberData(nameof(IndirectRelationLookup))]
    public async Task IndirectRelationLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
     public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
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
            new ConcurrentBag<string>([
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
            new ConcurrentBag<string>([
                TestsConsts.Users.Alice,
            ])
        }
    };
    
    [Theory]
    [MemberData(nameof(SimplePermissionLookup))]
    public async Task SimplePermissionLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
        public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
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
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PublicWorkspace),
            new ConcurrentBag<string>([
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
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(false))
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PublicWorkspace),
            new ConcurrentBag<string>([
                
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
            new ConcurrentBag<string>([
                
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
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            ],
            new LookupSubjectRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Workspaces.PrivateWorkspace),
            new ConcurrentBag<string>([
            ])
        },
    };
    
    [Theory]
    [MemberData(nameof(IntersectWithRelationAndAttributePermissionLookup))]
    public async Task IntersectWithRelationAndAttributeLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
}