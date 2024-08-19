using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Schemas;
using Valtuutus.Data;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// This test collection asserts that reads are correctly filtering
/// relations/attributes for when they were deleted.
/// </summary>
public abstract class BaseSnapTokenSpecs : IAsyncLifetime
{
    protected BaseSnapTokenSpecs(IDatabaseFixture fixture)
    {
        Fixture = fixture;
    }

    protected IDatabaseFixture Fixture { get; }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    private ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(TestsConsts.Action);

        AddSpecificProvider(services)
            .AddConcurrentQueryLimit(3);

        return services.BuildServiceProvider();
    }

    private (IDataReaderProvider reader, IDataWriterProvider writer) CreateProviders()
    {
        var serviceProvider = CreateServiceProvider();
        var scope = serviceProvider.CreateScope();
        return (scope.ServiceProvider.GetRequiredService<IDataReaderProvider>(), scope.ServiceProvider.GetRequiredService<IDataWriterProvider>());
    }

    public static TheoryData<List<RelationTuple>, RelationTupleFilter, List<RelationTuple>>
        GetRelationsAfterFirstWriteData => new()
    {
        {
            [new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Bob ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Charlie ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Dan ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Eve ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Eve ),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsBrabos, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice )],
            new RelationTupleFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner",
                SnapToken = null
            },
            [new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Bob ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Charlie ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Dan ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Eve )]
        },
        {
            [new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Bob ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Charlie ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Dan ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Eve ),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Eve ),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsBrabos, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice )],
            new RelationTupleFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner",
                SubjectType = TestsConsts.Users.Identifier,
                SubjectId = TestsConsts.Users.Alice,
                SnapToken = null
            },
            [new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner", TestsConsts.Users.Identifier, TestsConsts.Users.Alice )]
        },

    };
    [Theory]
    [MemberData(nameof(GetRelationsAfterFirstWriteData))]
    public async Task GetRelations_After_First_Write_All_Relations_Should_be_Retrieved(List<RelationTuple> seedRelations, RelationTupleFilter filter, List<RelationTuple> expectedRelations)
    {
        // arrange
        var providers = CreateProviders();
        var snap = await providers.writer.Write(seedRelations, [], default);

        // act
        var relations = await providers.reader.GetRelations(filter with
        {
            SnapToken = snap
        }, default);

        // assert
        relations.Should().BeEquivalentTo(expectedRelations);
    }

    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}