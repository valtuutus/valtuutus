using System.Collections.Concurrent;
using Authorizee.Core;
using Authorizee.Core.Configuration;
using Authorizee.Core.Schemas;
using Authorizee.Data.Configuration;
using Authorizee.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class LookupEntityEngineSpecs : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public LookupEntityEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var serviceCollection = new ServiceCollection()
            .AddSingleton(Substitute.For<ILogger<PostgresAttributeReader>>())
            .AddSingleton(Substitute.For<ILogger<PostgresRelationTupleReader>>())
            .AddSingleton(Substitute.For<ILogger<LookupEntityEngine>>())
            .AddDatabaseSetup(_fixture.DbFactory, o => o.AddPostgres())
            .AddSchemaConfiguration(TestsConsts.Action);
        if (schema != null)
        {
            var serviceDescriptor = serviceCollection.First(descriptor => descriptor.ServiceType == typeof(Schema));
            serviceCollection.Remove(serviceDescriptor);
            serviceCollection.AddSingleton(schema);
        }

        return serviceCollection.BuildServiceProvider();
    }
    
    
    private async Task<LookupEntityEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        await Task.WhenAll(_fixture.DbFactory.InsertRelations(tuples), _fixture.DbFactory.InsertAttributes(attributes));
        var checkEngine = serviceProvider.CreateScope().ServiceProvider.GetRequiredService<LookupEntityEngine>();
        return checkEngine;
    }

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
    
    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.ResetDatabaseAsync();
    }
}