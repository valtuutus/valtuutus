using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Valtuutus.Data.Tests.Shared;


public abstract class DataSpecificDataEngineSpecs : IAsyncLifetime
{
    protected IDatabaseFixture _fixture = null!;
    protected readonly ServiceProvider _provider = null!;

    protected DataSpecificDataEngineSpecs()
    {
        _provider = CreateServiceProvider();
    }

    protected abstract void AddSpecificProvider(IValtuutusDataBuilder builder);

    private ServiceProvider CreateServiceProvider()
    {
        var builder = new ServiceCollection()
            .AddValtuutusCore(TestsConsts.Action)
            .AddValtuutusData();
        
        AddSpecificProvider(builder);
        var serviceCollection = builder.Services;

        return serviceCollection.BuildServiceProvider();
    }

    protected abstract Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples();
    
    public static TheoryData<RelationTuple[], AttributeTuple[],
        DeleteFilter, RelationTuple[], AttributeTuple[]> DeleteCases =
        new()
        {
            // Should delete everything, because no parameters has been passed to the filter.
            {
                [new RelationTuple("project", "1", "member", "user", "1"),
                    new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")],
                [], new DeleteFilter
                {
                    Relations = new []{ new DeleteRelationsFilter()}
                }, [], []
            },
            // Should delete only the relation with the given subject id.
            {
                [new RelationTuple("project", "1", "member", "user", "1"),
                    new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")],                [], new DeleteFilter
                {
                    Relations = new []{ new DeleteRelationsFilter{SubjectId = "1"}}
                }, [], []
            },
            // Should delete only the relation with the given entity id.
            {
                [new RelationTuple("project", "1", "member", "user", "1"),
                    new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")],
                [], new DeleteFilter
                {
                    Relations = new []{ new DeleteRelationsFilter{EntityId = "1"}}
                }, [ new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")], []
            },
            // Should delete only the relation with the given entity id and subject id.
            {
                [new RelationTuple("project", "1", "member", "user", "1"),
                    new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")],
                [], new DeleteFilter
                {
                    Relations = new []{ new DeleteRelationsFilter{EntityId = "1", SubjectId = "1"}}
                }, [new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")], []
            },
            // Should delete only the relation with the given entity id and entity type project.
            {
                [new RelationTuple("project", "1", "member", "user", "1"),
                    new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")],
                [], new DeleteFilter
                {
                    Relations = new []{ new DeleteRelationsFilter{EntityId = "1", EntityType = "project"}}
                }, [new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")], []
            },
            // should delete only the relations with the given subject id and subject type user.
            {
                [new RelationTuple("project", "1", "member", "user", "1"),
                    new RelationTuple("project", "2", "member", "user", "1"),
                    new RelationTuple("project", "3", "member", "user", "1")],
                [], new DeleteFilter
                {
                    Relations = new []{ new DeleteRelationsFilter{SubjectId = "1", SubjectType = "user"}}
                }, [], []
            },
            
            // Should delete every attribute of the specified entityId.
            {
                [], [new AttributeTuple("project", "1", "name", JsonValue.Create("test")!),
                    new AttributeTuple("project", "1", "public", JsonValue.Create(true)!),
                    new AttributeTuple("project", "1", "unreliable", JsonValue.Create(false)!),
                    new AttributeTuple("project", "2", "name", JsonValue.Create("test")!),
                    new AttributeTuple("project", "3", "name", JsonValue.Create("test")!)], new DeleteFilter
                {
                    Attributes = new []{ new DeleteAttributesFilter
                    {
                        EntityType = "project",
                        EntityId = "1"
                    }}
                }, [], [new AttributeTuple("project", "2", "name", JsonValue.Create("test")!),
                    new AttributeTuple("project", "3", "name", JsonValue.Create("test")!)]
            },
            
            // Should only delete the attribute with name public of the specified entityId.
            {
                [], [new AttributeTuple("project", "1", "name", JsonValue.Create("test")!),
                    new AttributeTuple("project", "1", "public", JsonValue.Create(true)!),
                    new AttributeTuple("project", "1", "unreliable", JsonValue.Create(false)!),
                    new AttributeTuple("project", "2", "name", JsonValue.Create("test")!),
                    new AttributeTuple("project", "3", "name", JsonValue.Create("test")!)], new DeleteFilter
                {
                    Attributes = new []{ new DeleteAttributesFilter
                    {
                        EntityType = "project",
                        EntityId = "1",
                        Attribute = "public"
                    }}
                }, [], [new AttributeTuple("project", "1", "name", JsonValue.Create("test")!),
                    new AttributeTuple("project", "1", "unreliable", JsonValue.Create(false)!),
                    new AttributeTuple("project", "2", "name", JsonValue.Create("test")!),
                    new AttributeTuple("project", "3", "name", JsonValue.Create("test")!)]
            },
        };

    [Theory]
    [MemberData(nameof(DeleteCases))]
    public async Task AfterDeletionDataShouldBeExpected(RelationTuple[] seedRelations, AttributeTuple[] seedAttributes,
        DeleteFilter filter, RelationTuple[] expectedTuples, AttributeTuple[] expectedAttributes)
    {
        // arrange
        var engine = _provider.GetRequiredService<DataEngine>();
        await engine.Write(seedRelations, seedAttributes, default);
        
        // act
        await engine.Delete(filter, default);
        
        // assert
        var (relations, attributes) = await GetCurrentTuples();

        relations.Should().BeEquivalentTo(expectedTuples);
        attributes.Select(x => new
        {
            x.Attribute,
            x.EntityType,
            x.EntityId,
            Value = x.Value.ToJsonString()
        }).Should().BeEquivalentTo(expectedAttributes.Select(x => new
        {
            x.Attribute,
            x.EntityType,
            x.EntityId,
            Value = x.Value.ToJsonString()
        }));

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