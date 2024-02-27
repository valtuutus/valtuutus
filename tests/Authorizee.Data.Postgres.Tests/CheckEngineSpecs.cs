using Authorizee.Core;
using Authorizee.Core.Configuration;
using Authorizee.Core.Schemas;
using Authorizee.Data.Configuration;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Authorizee.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public class CheckEngineSpecs : IAsyncDisposable
{
    private readonly PostgresFixture _fixture;

    public CheckEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }
    
    private ServiceProvider CreateServiceProvider()
    {
        var serviceProvider = new ServiceCollection()
            .AddSingleton(Substitute.For<ILogger<PostgresAttributeReader>>())
            .AddSingleton(Substitute.For<ILogger<PostgresRelationTupleReader>>())
            .AddSingleton(Substitute.For<ILogger<CheckEngine>>())
            .AddDatabaseSetup(_fixture.DbFactory, o => o.AddPostgres())
            .AddSchemaConfiguration(c =>
            {

            }).BuildServiceProvider();

        return serviceProvider;
    }
    private async Task<CheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {

        var serviceProvider = CreateServiceProvider();
        var checkEngine = serviceProvider.CreateScope().ServiceProvider.GetRequiredService<CheckEngine>();
        return checkEngine;
    }
    
    [Fact]
    public async Task FixtureIsWorking()
    {
        // arrange
        var engine = await CreateEngine([], []);
        
        // act
        var result = await engine.Check(new CheckRequest
        {
            EntityId = "1",
            EntityType = "project",
            Permission = "view",
            SubjectType = "user",
            SubjectId = "1"
        }, default);
        
        // assert
        result.Should().BeFalse();
    }

    public async ValueTask DisposeAsync()
    {
        await _fixture.ResetDatabaseAsync();
    }
}