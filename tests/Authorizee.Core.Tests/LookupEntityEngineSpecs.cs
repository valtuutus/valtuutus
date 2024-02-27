using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Authorizee.Core.Schemas;
using Authorizee.Tests.Shared;
using FluentAssertions;

namespace Authorizee.Core.Tests;

public sealed class LookupEntityEngineSpecs
{
    public static LookupEntityEngine CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var relationTupleReader = new InMemoryRelationTupleReader(tuples);
        var attributeReader = new InMemoryAttributeTupleReader(attributes);
        return new LookupEntityEngine(schema ?? TestsConsts.Schemas, relationTupleReader, attributeReader);
    }

    public static TheoryData<RelationTuple[], AttributeTuple[], LookupEntityRequest, ConcurrentBag<string>>
        TopLevelChecks = LookupEntityEngineSpecList.TopLevelChecks;

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
        IndirectRelationLookup = LookupEntityEngineSpecList.IndirectRelationLookup;

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
        SimplePermissionLookup = LookupEntityEngineSpecList.SimplePermissionLookup;
    
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
        IntersectWithRelationAndAttributePermissionLookup = LookupEntityEngineSpecList.IntersectWithRelationAndAttributePermissionLookup;
    
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