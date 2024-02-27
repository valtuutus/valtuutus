using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Authorizee.Core.Schemas;
using Authorizee.Tests.Shared;
using FluentAssertions;

namespace Authorizee.Core.Tests;

public sealed class LookupSubjectEngineSpecs
{
    public static LookupSubjectEngine CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes,
        Schema? schema = null)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        return new LookupSubjectEngine(schema ?? TestsConsts.Schemas, relationTupleReader, attributeReader);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupSubjectRequest, ConcurrentBag<string>>
        TopLevelChecks => LookupSubjectEngineSpecList.TopLevelChecks;
    
    
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
        IndirectRelationLookup = LookupSubjectEngineSpecList.IndirectRelationLookup;

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
        SimplePermissionLookup = LookupSubjectEngineSpecList.SimplePermissionLookup;
    
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
        IntersectWithRelationAndAttributePermissionLookup = LookupSubjectEngineSpecList.IntersectWithRelationAndAttributePermissionLookup;
    
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