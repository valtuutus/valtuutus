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
        return (scope.ServiceProvider.GetRequiredService<IDataReaderProvider>(),
            scope.ServiceProvider.GetRequiredService<IDataWriterProvider>());
    }

    public static TheoryData<List<RelationTuple>, RelationTupleFilter, List<RelationTuple>>
        GetRelationsAfterFirstWriteData => new()
    {
        {
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Dan),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Eve),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Eve),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsBrabos, "owner", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice)
            ],
            new RelationTupleFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner",
                SnapToken = null
            },
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Dan),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Eve)
            ]
        },
        {
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Dan),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Eve),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PrivateWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Eve),
                new(TestsConsts.Teams.Identifier, TestsConsts.Teams.OsBrabos, "owner", TestsConsts.Users.Identifier,
                    TestsConsts.Users.Alice)
            ],
            new RelationTupleFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner",
                SubjectType = TestsConsts.Users.Identifier,
                SubjectId = TestsConsts.Users.Alice,
                SnapToken = null
            },
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ]
        },
    };

    [Theory]
    [MemberData(nameof(GetRelationsAfterFirstWriteData))]
    public async Task GetRelations_After_First_Write_All_Relations_Should_be_Retrieved(
        List<RelationTuple> seedRelations, RelationTupleFilter filter, List<RelationTuple> expectedRelations)
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

    [Theory]
    [MemberData(nameof(GetRelationsAfterWriteAndDeleteData))]
    public async Task GetRelations_After_Write_And_Delete_Should_Reflect_Correct_State(
        List<RelationTuple> seedRelations,
        DeleteFilter deleteFilter,
        RelationTupleFilter initialFilter,
        List<RelationTuple> expectedRelationsAfterDeletion)
    {
        // Arrange
        var providers = CreateProviders();

        // Act - Initial Write
        var initialSnapToken = await providers.writer.Write(seedRelations, new List<AttributeTuple>(), default);

        // Assert - Validate relations after initial write
        var initialRelations = await providers.reader.GetRelations(initialFilter with
        {
            SnapToken = initialSnapToken
        }, default);
        initialRelations.Should().BeEquivalentTo(seedRelations);

        // Act - Perform deletion using the DeleteFilter
        var deleteSnapToken = await providers.writer.Delete(deleteFilter, default);

        // Assert - Validate relations after deletion
        var relationsAfterDeletion = await providers.reader.GetRelations(initialFilter with
        {
            SnapToken = deleteSnapToken
        }, default);
        relationsAfterDeletion.Should().BeEquivalentTo(expectedRelationsAfterDeletion);
    }

    public static TheoryData<List<RelationTuple>, DeleteFilter, RelationTupleFilter, List<RelationTuple>>
        GetRelationsAfterWriteAndDeleteData => new()
    {
        {
            // Seed Relations
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            },
            // Delete Filter
            new DeleteFilter
            {
                Relations = new[]
                {
                    new DeleteRelationsFilter
                    {
                        EntityType = TestsConsts.Workspaces.Identifier,
                        EntityId = TestsConsts.Workspaces.PublicWorkspace,
                        Relation = "owner",
                        SubjectType = TestsConsts.Users.Identifier,
                        SubjectId = TestsConsts.Users.Bob
                    }
                }
            },
            // Initial Filter
            new RelationTupleFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner",
                SnapToken = null
            },
            // Expected Relations After Deletion
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            }
        }
    };

    [Theory]
    [MemberData(nameof(GetRelationsWithOlderSnapTokenData))]
    public async Task GetRelations_With_Older_SnapToken_Should_Not_Include_Newer_Data(
        List<RelationTuple> seedRelations,
        List<RelationTuple> newRelations,
        RelationTupleFilter filter,
        List<RelationTuple> expectedRelationsWithOldSnapToken,
        List<RelationTuple> expectedRelationsWithNewSnapToken)
    {
        // Arrange
        var providers = CreateProviders();

        // Act - Initial Write
        var initialSnapToken = await providers.writer.Write(seedRelations, new List<AttributeTuple>(), default);

        // Act - Add new relations
        var newSnapToken = await providers.writer.Write(newRelations, new List<AttributeTuple>(), default);

        // Assert - Validate relations with the old snap token
        var relationsWithOldSnapToken = await providers.reader.GetRelations(filter with
        {
            SnapToken = initialSnapToken
        }, default);
        relationsWithOldSnapToken.Should().BeEquivalentTo(expectedRelationsWithOldSnapToken);

        // Assert - Validate relations with the new snap token
        var relationsWithNewSnapToken = await providers.reader.GetRelations(filter with
        {
            SnapToken = newSnapToken
        }, default);
        relationsWithNewSnapToken.Should().BeEquivalentTo(expectedRelationsWithNewSnapToken);
    }

    public static TheoryData<List<RelationTuple>, List<RelationTuple>, RelationTupleFilter, List<RelationTuple>,
            List<RelationTuple>>
        GetRelationsWithOlderSnapTokenData => new()
    {
        {
            // Seed Relations
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            },
            // New Relations (added later)
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie)
            },
            // Filter to apply for both old and new snap tokens
            new RelationTupleFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner",
                SnapToken = null // We'll override this dynamically in the test
            },
            // Expected Relations with the Old SnapToken
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            },
            // Expected Relations with the New SnapToken
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie)
            }
        }
    };

    [Theory]
    [MemberData(nameof(GetRelationsWithEntityIdsData))]
    public async Task GetRelationsWithEntityIds_Should_Respect_SnapToken(
        List<RelationTuple> seedRelations,
        EntityRelationFilter entityRelationFilter,
        string subjectType,
        List<string> entityIds,
        string? subjectRelation,
        List<RelationTuple> expectedRelations)
    {
        // Arrange
        var providers = CreateProviders();

        // Act - Initial Write
        var initialSnapToken = await providers.writer.Write(seedRelations, new List<AttributeTuple>(), default);

        // Assert - Validate relations after initial write
        var initialRelations = await providers.reader.GetRelationsWithEntityIds(
            entityRelationFilter with { SnapToken = initialSnapToken },
            subjectType,
            entityIds,
            subjectRelation,
            default
        );
        initialRelations.Should().BeEquivalentTo(seedRelations);

        // Perform additional operations (e.g., deletion) here if needed and validate against older SnapTokens
        // Assert - Validate relations for older SnapToken
        var olderRelations = await providers.reader.GetRelationsWithEntityIds(
            entityRelationFilter with { SnapToken = new SnapToken("older-token") }, // Simulate an older SnapToken
            subjectType,
            entityIds,
            subjectRelation,
            default
        );
        olderRelations.Should().BeEquivalentTo(expectedRelations);
    }

    public static
        TheoryData<List<RelationTuple>, EntityRelationFilter, string, List<string>, string?, List<RelationTuple>>
        GetRelationsWithEntityIdsData =>
        new()
        {
            {
                // Test Case 1: SnapToken includes all matching relations
                new List<RelationTuple>
                {
                    new("workspace", "public", "owner", "user", "alice"),
                    new("workspace", "private", "owner", "user", "charlie"),
                    new("workspace", "private", "member", "user", "bob")
                },
                new EntityRelationFilter
                {
                    EntityType = "workspace",
                    Relation = "owner",
                    SnapToken = null // Latest SnapToken
                },
                "user", // subjectType
                new List<string> { "public", "private" }, // entityIds
                null, // subjectRelation
                new List<RelationTuple>
                {
                    new("workspace", "public", "owner", "user", "alice"),
                    new("workspace", "private", "owner", "user", "charlie"),
                }
            },
            {
                // Test Case 2: Older SnapToken excludes newer relations
                new List<RelationTuple>
                {
                    new("workspace", "public", "owner", "user", "alice"),
                },
                new EntityRelationFilter
                {
                    EntityType = "workspace",
                    Relation = "owner",
                    SnapToken = null // Latest SnapToken
                },
                "user", // subjectType
                new List<string> { "public" }, // entityIds
                null, // subjectRelation
                new List<RelationTuple>
                {
                    new("workspace", "public", "owner", "user", "alice")
                }
            }
        };

    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}