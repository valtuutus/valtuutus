using System.Collections.Concurrent;
using Authorizee.Core;
using Authorizee.Core.Schemas;
using FluentAssertions;

namespace Authorizee.Tests.Shared;

public abstract class BaseLookupSubjectEngineSpecs
{
    protected abstract ValueTask<LookupSubjectEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null);
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
        TopLevelChecks => LookupSubjectEngineSpecList.TopLevelChecks;
    
    
    [Theory]
    [MemberData(nameof(TopLevelChecks))]
    public async Task TopLevelCheckShouldReturnExpectedResult(RelationTuple[] tuples, AttributeTuple[] attributes,
        LookupSubjectRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
        IndirectRelationLookup = LookupSubjectEngineSpecList.IndirectRelationLookup;

    [Theory]
    [MemberData(nameof(IndirectRelationLookup))]
    public async Task IndirectRelationLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
        SimplePermissionLookup = LookupSubjectEngineSpecList.SimplePermissionLookup;
    
    [Theory]
    [MemberData(nameof(SimplePermissionLookup))]
    public async Task SimplePermissionLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
    
    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
        IntersectWithRelationAndAttributePermissionLookup = LookupSubjectEngineSpecList.IntersectWithRelationAndAttributePermissionLookup;
    
    [Theory]
    [MemberData(nameof(IntersectWithRelationAndAttributePermissionLookup))]
    public async Task IntersectWithRelationAndAttributeLookupShouldReturnExpectedResult(RelationTuple[] tuples,
        AttributeTuple[] attributes, LookupSubjectRequest request, ConcurrentBag<string> expected)
    {
        // Arrange
        var engine = await CreateEngine(tuples, attributes);

        // Act
        var result = await engine.Lookup(request, default);

        // assert
        result.Should().BeEquivalentTo(expected);
    }
}