using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Authorizee.Core;
using Authorizee.Core.Schemas;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Tests;

public class LookupEntityEngineSpecs
{
    public static LookupEntityEngine CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        var logger = Substitute.For<ILogger<LookupEntityEngine>>();
        return new LookupEntityEngine(schema ?? TestsConsts.Schemas, logger, relationTupleReader, attributeReader);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        TopLevelChecks => new()
    {
        {
            // Checks direct relation
            new RelationTuple[]
            {
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Admins, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice),
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new LookupEntityRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Groups.Admins, TestsConsts.Groups.Designers, TestsConsts.Groups.Developers
            ])
        },
        {
            // Checks direct relation, but alice is not a part of the group
            new RelationTuple[]
            {
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Bob),
            },
            new AttributeTuple[]
            {
            },
            new LookupEntityRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([])
        },
        {
            // Checks attribute
            new RelationTuple[]
            {
            },
            new AttributeTuple[]
            {
            },
            new LookupEntityRequest(TestsConsts.Groups.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([])
        },
    };

    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupEntityRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        IndirectRelationLookup => new()
    {
        {
            // Checks indirect relation
            new RelationTuple[]
            {
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsMaisBrabos, "member",
                    TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member"),
                
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsBrabos, "member",
                    TestsConsts.Groups.Identifier, TestsConsts.Groups.Designers, "member"),
            },
            new AttributeTuple[]
            {
            },
            new LookupEntityRequest(TestsConsts.Teams.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Teams.OsMaisBrabos,
                TestsConsts.Teams.OsBrabos,
            ])
        },
        {
            // Checks indirect and direct relations
            new RelationTuple[]
            {
                new(TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsMaisBrabos, "member",
                    TestsConsts.Groups.Identifier, TestsConsts.Groups.Developers, "member"),
                
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsBrabos, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new LookupEntityRequest(TestsConsts.Teams.Identifier, "member", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Teams.OsMaisBrabos,
                TestsConsts.Teams.OsBrabos,
            ])
        },
    };

    [Theory]
    [MemberData(nameof(IndirectRelationLookup))]
    public async Task IndirectRelationLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        SimplePermissionLookup => new()
    {
        {
            // Checks indirect relation
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "delete", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Workspaces.PublicWorkspace,
                TestsConsts.Workspaces.PrivateWorkspace,
            ])
        },
        {
            // Checks indirect relation
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "delete", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Workspaces.PrivateWorkspace,
            ])
        },
    };
    
    [Theory]
    [MemberData(nameof(SimplePermissionLookup))]
    public async Task SimplePermissionLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        IntersectWithRelationAndAttributePermissionLookup => new()
    {
        {
            // with public attribute
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(true))
            },
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([
                TestsConsts.Workspaces.PublicWorkspace,
            ])
        },
        {
            // without public attribute
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
            },
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([])
        },
        {
            // without public attribute as false
            new RelationTuple[]
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "member",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
            },
            new AttributeTuple[]
            {
                new AttributeTuple(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "public", JsonValue.Create(false))
            },
            new LookupEntityRequest(TestsConsts.Workspaces.Identifier, "comment", TestsConsts.Users.Identifier,
                TestsConsts.Users.Alice),
            new ConcurrentBag<string>([])
        },
    };
    
    [Theory]
    [MemberData(nameof(IntersectWithRelationAndAttributePermissionLookup))]
    public async Task IntersectWithRelationAndAttributeLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
}