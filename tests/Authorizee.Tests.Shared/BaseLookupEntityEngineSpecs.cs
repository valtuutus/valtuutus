using System.Collections.Concurrent;
using Authorizee.Core;
using Authorizee.Core.Schemas;
using FluentAssertions;

namespace Authorizee.Tests.Shared;

public abstract class BaseLookupEntityEngineSpecs
{
    protected abstract ValueTask<LookupEntityEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        Schema? schema = null);
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        TopLevelChecks = LookupEntityEngineSpecList.TopLevelChecks;

    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupEntityRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        IndirectRelationLookup = LookupEntityEngineSpecList.IndirectRelationLookup;

    [Theory]
    [MemberData(nameof(IndirectRelationLookup))]
    public async Task IndirectRelationLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        SimplePermissionLookup = LookupEntityEngineSpecList.SimplePermissionLookup;
    
    [Theory]
    [MemberData(nameof(SimplePermissionLookup))]
    public async Task SimplePermissionLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        IntersectWithRelationAndAttributePermissionLookup = LookupEntityEngineSpecList.IntersectWithRelationAndAttributePermissionLookup;
    
    [Theory]
    [MemberData(nameof(IntersectWithRelationAndAttributePermissionLookup))]
    public async Task IntersectWithRelationAndAttributeLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupEntityRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.LookupEntity(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
}