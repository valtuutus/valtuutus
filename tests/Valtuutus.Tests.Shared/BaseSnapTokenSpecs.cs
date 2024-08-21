using System.Text.Json.Nodes;
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
        var providers = CreateProviders();

        var initialSnapToken = await providers.writer.Write(seedRelations, new List<AttributeTuple>(), default);

        var initialRelations = await providers.reader.GetRelations(initialFilter with
        {
            SnapToken = initialSnapToken
        }, default);
        initialRelations.Should().BeEquivalentTo(seedRelations);

        var deleteSnapToken = await providers.writer.Delete(deleteFilter, default);

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
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            },
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
            new RelationTupleFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner",
                SnapToken = null
            },
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
        var providers = CreateProviders();

        var initialSnapToken = await providers.writer.Write(seedRelations, new List<AttributeTuple>(), default);

        var newSnapToken = await providers.writer.Write(newRelations, new List<AttributeTuple>(), default);

        var relationsWithOldSnapToken = await providers.reader.GetRelations(filter with
        {
            SnapToken = initialSnapToken
        }, default);
        relationsWithOldSnapToken.Should().BeEquivalentTo(expectedRelationsWithOldSnapToken);

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
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            },
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Charlie)
            },
            new RelationTupleFilter
            {
                EntityType = TestsConsts.Workspaces.Identifier,
                EntityId = TestsConsts.Workspaces.PublicWorkspace,
                Relation = "owner",
                SnapToken = null
            },
            new List<RelationTuple>
            {
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice),
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Bob)
            },
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

    [Fact]
    public async Task GetRelationsWithEntityIds_Should_Respect_SnapToken()
    {
        var providers = CreateProviders();

        var seed = new List<RelationTuple>
        {
            new("workspace", "public", "owner", "user", "alice"),
            new("workspace", "private", "member", "user", "bob")
        };
        var filter = new EntityRelationFilter
        {
            EntityType = "workspace",
            Relation = "owner",
            SnapToken = null
        };
        var initialSnapToken = await providers.writer.Write(seed, new List<AttributeTuple>(), default);

        var newSnapToken = await providers.writer.Write(
            [new RelationTuple("workspace", "private", "owner", "user", "charlie")], new List<AttributeTuple>(),
            default);

        // Assert that using an older token does not bring new data
        var relationsWithOldToken = await providers.reader.GetRelationsWithEntityIds(
            filter with { SnapToken = initialSnapToken },
            "user",
            new List<string> { "public", "private" },
            null,
            default
        );

        relationsWithOldToken.Should().BeEquivalentTo(new List<RelationTuple>
        {
            new("workspace", "public", "owner", "user", "alice"),
        });

        // Assert that the newer token returns the inserted data
        var relationsWithNewToken = await providers.reader.GetRelationsWithEntityIds(
            filter with { SnapToken = newSnapToken },
            "user",
            new List<string> { "public", "private" },
            null,
            default
        );
        relationsWithNewToken.Should().BeEquivalentTo(new List<RelationTuple>
        {
            new("workspace", "public", "owner", "user", "alice"),
            new("workspace", "private", "owner", "user", "charlie"),
        });
    }

    [Fact]
    public async Task GetRelationsWithSubjectsIds_Should_Respect_SnapToken()
    {
        var providers = CreateProviders();

        var seed = new List<RelationTuple>
        {
            new("workspace", "public", "owner", "user", "alice"),
            new("workspace", "private", "member", "user", "bob")
        };
        var filter = new EntityRelationFilter
        {
            EntityType = "workspace",
            Relation = "owner",
            SnapToken = null
        };
        var initialSnapToken = await providers.writer.Write(seed, new List<AttributeTuple>(), default);

        var newSnapToken = await providers.writer.Write(
            [new RelationTuple("workspace", "private", "owner", "user", "charlie")], new List<AttributeTuple>(),
            default);

        // Assert that using an older token does not bring new data
        var relationsWithOldToken = await providers.reader.GetRelationsWithSubjectsIds(
            filter with { SnapToken = initialSnapToken },
            new List<string> { "charlie", "alice", "bob" },
            "user",
            default
        );

        relationsWithOldToken.Should().BeEquivalentTo(new List<RelationTuple>
        {
            new("workspace", "public", "owner", "user", "alice"),
        });

        // Assert that the newer token returns the inserted data
        var relationsWithNewToken = await providers.reader.GetRelationsWithSubjectsIds(
            filter with { SnapToken = newSnapToken },
            new List<string> { "charlie", "alice", "bob" },
            "user",
            default
        );
        relationsWithNewToken.Should().BeEquivalentTo(new List<RelationTuple>
        {
            new("workspace", "public", "owner", "user", "alice"),
            new("workspace", "private", "owner", "user", "charlie"),
        });
    }

    [Fact]
    public async Task GetAttribute_Should_Respect_SnapToken()
    {
        var providers = CreateProviders();

        var seed = new List<AttributeTuple>
        {
            new("workspace", "maneirinho", "public", JsonValue.Create(false)),
        };

        var initialSnapToken = await providers.writer.Write([], seed, default);

        var newSnapToken = await providers.writer.Write(
            [], [new AttributeTuple("workspace", "daora", "public", JsonValue.Create(false))],
            default);

        // Assert that using an older token does not bring new data
        var attrWithOldToken = await providers.reader.GetAttribute(new EntityAttributeFilter
            {
                EntityType = "workspace",
                Attribute = "public",
                SnapToken = initialSnapToken,
                EntityId = "daora"
            },
            default
        );

        attrWithOldToken.Should().BeNull();

        // Assert that the newer token returns the inserted data
        var attrWithNewToken = await providers.reader.GetAttribute(new EntityAttributeFilter
            {
                EntityType = "workspace",
                Attribute = "public",
                SnapToken = newSnapToken,
                EntityId = "daora"
            },
            default
        );
        new
        {
            attrWithNewToken!.EntityType,
            attrWithNewToken!.EntityId,
            attrWithNewToken!.Attribute,
            Value = attrWithNewToken!.Value.ToJsonString()
        }.Should().BeEquivalentTo(new
        {
            EntityType = "workspace",
            EntityId = "daora",
            Attribute = "public",
            Value = JsonValue.Create(false).ToJsonString()
        });
    }

    [Fact]
    public async Task GetAttributes_Should_Respect_SnapToken()
    {
        var providers = CreateProviders();

        var seed = new List<AttributeTuple>
        {
            new("workspace", "maneirinho", "public", JsonValue.Create(false)),
        };

        var initialSnapToken = await providers.writer.Write([], seed, default);

        var newSnapToken = await providers.writer.Write(
            [], [new AttributeTuple("workspace", "daora", "public", JsonValue.Create(false))],
            default);

        // Assert that using an older token does not bring new data
        var attrWithOldToken = await providers.reader.GetAttributes(new EntityAttributeFilter
            {
                EntityType = "workspace",
                Attribute = "public",
                SnapToken = initialSnapToken,
            },
            default
        );

        attrWithOldToken
            .Select(a => new { a.EntityId, a.Attribute, a.EntityType, Value = a.Value.ToJsonString() })
            .Should().BeEquivalentTo([
                new
                {
                    EntityType = "workspace",
                    Attribute = "public",
                    EntityId = "maneirinho",
                    Value = JsonValue.Create(false).ToJsonString()
                }
            ]);

        // Assert that the newer token returns the inserted data
        var attrWithNewToken = await providers.reader.GetAttributes(new EntityAttributeFilter
            {
                EntityType = "workspace",
                Attribute = "public",
                SnapToken = newSnapToken,
            },
            default
        );
        attrWithNewToken
            .Select(a => new { a.EntityId, a.Attribute, a.EntityType, Value = a.Value.ToJsonString() })
            .Should().BeEquivalentTo([
                new
                {
                    EntityType = "workspace",
                    Attribute = "public",
                    EntityId = "maneirinho",
                    Value = JsonValue.Create(false).ToJsonString()
                },
                new
                {
                    EntityType = "workspace",
                    Attribute = "public",
                    EntityId = "daora",
                    Value = JsonValue.Create(false).ToJsonString()
                },
            ]);
    }

    [Fact]
    public async Task GetAttributesWithEntityIds_Should_Respect_SnapToken()
    {
        var providers = CreateProviders();

        var seed = new List<AttributeTuple>
        {
            new("workspace", "maneirinho", "public", JsonValue.Create(false)),
        };

        var initialSnapToken = await providers.writer.Write([], seed, default);

        var newSnapToken = await providers.writer.Write(
            [], [new AttributeTuple("workspace", "daora", "public", JsonValue.Create(false))],
            default);

        // Assert that using an older token does not bring new data
        var attrWithOldToken = await providers.reader.GetAttributesWithEntityIds(
            new AttributeFilter
            {
                EntityType = "workspace",
                Attribute = "public",
                SnapToken = initialSnapToken,
            },
            ["daora", "maneirinho"],
            default
        );

        attrWithOldToken
            .Select(a => new { a.EntityId, a.Attribute, a.EntityType, Value = a.Value.ToJsonString() })
            .Should().BeEquivalentTo([
                new
                {
                    EntityType = "workspace",
                    Attribute = "public",
                    EntityId = "maneirinho",
                    Value = JsonValue.Create(false).ToJsonString()
                }
            ]);

        // Assert that the newer token returns the inserted data
        var attrWithNewToken = await providers.reader.GetAttributesWithEntityIds(
            new AttributeFilter
            {
                EntityType = "workspace",
                Attribute = "public",
                SnapToken = newSnapToken,
            },
            ["daora", "maneirinho"],
            default
        );
        attrWithNewToken
            .Select(a => new { a.EntityId, a.Attribute, a.EntityType, Value = a.Value.ToJsonString() })
            .Should().BeEquivalentTo([
                new
                {
                    EntityType = "workspace",
                    Attribute = "public",
                    EntityId = "maneirinho",
                    Value = JsonValue.Create(false).ToJsonString()
                },
                new
                {
                    EntityType = "workspace",
                    Attribute = "public",
                    EntityId = "daora",
                    Value = JsonValue.Create(false).ToJsonString()
                },
            ]);
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