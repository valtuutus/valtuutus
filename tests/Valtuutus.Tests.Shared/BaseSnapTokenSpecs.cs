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
            .AddValtuutusCore(TestsConsts.DefaultSchema);

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
                SnapToken = SnapToken.MinValue
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
                SnapToken = SnapToken.MinValue
            },
            [
                new(TestsConsts.Workspaces.Identifier, TestsConsts.Workspaces.PublicWorkspace, "owner",
                    TestsConsts.Users.Identifier, TestsConsts.Users.Alice)
            ]
        },
    };

    [Fact]
    public async Task GetLatestSnapToken_Should_Return_Null_When_Empty()
    {
        var providers = CreateProviders();

        var latest = await providers.reader.GetLatestSnapToken(default);

        latest.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSnapToken_Should_Return_Latest_After_Writes()
    {
        var providers = CreateProviders();

        var first = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "user", "alice")
        ], [], default);

        var second = await providers.writer.Write([
            new RelationTuple("workspace", "private", "owner", "user", "bob")
        ], [], default);

        var latest = await providers.reader.GetLatestSnapToken(default);

        latest.Should().Be(second);
        latest.Should().NotBe(first);
    }

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
                SnapToken = SnapToken.MinValue
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
                SnapToken = SnapToken.MinValue
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
            SnapToken = SnapToken.MinValue
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
            SnapToken = SnapToken.MinValue
        };
        var initialSnapToken = await providers.writer.Write(seed, new List<AttributeTuple>(), default);

        var newSnapToken = await providers.writer.Write(
            [new RelationTuple("workspace", "private", "owner", "user", "charlie")], new List<AttributeTuple>(),
            default);

        // Assert that using an older token does not bring new data
        var relationsWithOldToken = await providers.reader.GetRelationsWithSubjectsIds(
            filter with { SnapToken = initialSnapToken },
            ["charlie", "alice", "bob"],
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
            ["charlie", "alice", "bob"],
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
    public async Task HasAnyDirectRelation_Should_Return_True_When_Any_Entity_Has_Direct_Relation()
    {
        var providers = CreateProviders();

        var snapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "user", "alice"),
            new RelationTuple("workspace", "private", "owner", "user", "bob")
        ], [], default);

        var result = await providers.reader.HasAnyDirectRelation(
            "workspace",
            ["missing", "private", "other"],
            "owner",
            "bob",
            snapToken,
            default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAnyDirectRelation_Should_Ignore_Indirect_Tuples()
    {
        var providers = CreateProviders();

        var snapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "group", "admins", "member")
        ], [], default);

        var result = await providers.reader.HasAnyDirectRelation(
            "workspace",
            ["public"],
            "owner",
            "admins",
            snapToken,
            default);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAnyDirectRelation_Should_Respect_SnapToken()
    {
        var providers = CreateProviders();

        var firstSnapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "user", "alice")
        ], [], default);

        var secondSnapToken = await providers.writer.Write([
            new RelationTuple("workspace", "private", "owner", "user", "charlie")
        ], [], default);

        var resultWithOldToken = await providers.reader.HasAnyDirectRelation(
            "workspace",
            ["private"],
            "owner",
            "charlie",
            firstSnapToken,
            default);

        var resultWithNewToken = await providers.reader.HasAnyDirectRelation(
            "workspace",
            ["private"],
            "owner",
            "charlie",
            secondSnapToken,
            default);

        resultWithOldToken.Should().BeFalse();
        resultWithNewToken.Should().BeTrue();
    }

    [Fact]
    public async Task HasDirectRelation_Should_Return_True_For_Direct_Tuple()
    {
        var providers = CreateProviders();

        var snapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "user", "alice")
        ], [], default);

        var result = await providers.reader.HasDirectRelation(new RelationTupleFilter
            {
                EntityType = "workspace",
                EntityId = "public",
                Relation = "owner",
                SnapToken = snapToken
            },
            "alice",
            default);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasDirectRelation_Should_Ignore_Indirect_Tuple()
    {
        var providers = CreateProviders();

        var snapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "group", "admins", "member")
        ], [], default);

        var result = await providers.reader.HasDirectRelation(new RelationTupleFilter
            {
                EntityType = "workspace",
                EntityId = "public",
                Relation = "owner",
                SnapToken = snapToken
            },
            "admins",
            default);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasDirectRelation_Should_Respect_SnapToken()
    {
        var providers = CreateProviders();

        var firstSnapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "user", "alice")
        ], [], default);

        var secondSnapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "user", "bob")
        ], [], default);

        var resultWithOldToken = await providers.reader.HasDirectRelation(new RelationTupleFilter
            {
                EntityType = "workspace",
                EntityId = "public",
                Relation = "owner",
                SnapToken = firstSnapToken
            },
            "bob",
            default);

        var resultWithNewToken = await providers.reader.HasDirectRelation(new RelationTupleFilter
            {
                EntityType = "workspace",
                EntityId = "public",
                Relation = "owner",
                SnapToken = secondSnapToken
            },
            "bob",
            default);

        resultWithOldToken.Should().BeFalse();
        resultWithNewToken.Should().BeTrue();
    }

    [Fact]
    public async Task GetIndirectRelations_Should_Return_Only_Indirect_Tuples()
    {
        var providers = CreateProviders();

        var snapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "user", "alice"),
            new RelationTuple("workspace", "public", "owner", "group", "admins", "member")
        ], [], default);

        var relations = await providers.reader.GetIndirectRelations(new RelationTupleFilter
        {
            EntityType = "workspace",
            EntityId = "public",
            Relation = "owner",
            SnapToken = snapToken
        }, default);

        relations.Should().BeEquivalentTo([
            new RelationTuple("workspace", "public", "owner", "group", "admins", "member")
        ]);
    }

    [Fact]
    public async Task GetIndirectRelations_Should_Respect_SnapToken()
    {
        var providers = CreateProviders();

        var firstSnapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "group", "admins", "member")
        ], [], default);

        var secondSnapToken = await providers.writer.Write([
            new RelationTuple("workspace", "public", "owner", "group", "reviewers", "member")
        ], [], default);

        var relationsWithOldToken = await providers.reader.GetIndirectRelations(new RelationTupleFilter
        {
            EntityType = "workspace",
            EntityId = "public",
            Relation = "owner",
            SnapToken = firstSnapToken
        }, default);

        var relationsWithNewToken = await providers.reader.GetIndirectRelations(new RelationTupleFilter
        {
            EntityType = "workspace",
            EntityId = "public",
            Relation = "owner",
            SnapToken = secondSnapToken
        }, default);

        relationsWithOldToken.Should().BeEquivalentTo([
            new RelationTuple("workspace", "public", "owner", "group", "admins", "member")
        ]);
        relationsWithNewToken.Should().BeEquivalentTo([
            new RelationTuple("workspace", "public", "owner", "group", "admins", "member"),
            new RelationTuple("workspace", "public", "owner", "group", "reviewers", "member")
        ]);
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
    public async Task GetAttribute_Should_Return_Null_When_Missing()
    {
        var providers = CreateProviders();

        await providers.writer.Write([], [
            new AttributeTuple("workspace", "public", "status", JsonValue.Create("active")!)
        ], default);

        var attribute = await providers.reader.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "workspace",
            EntityId = "missing",
            Attribute = "status",
            SnapToken = SnapToken.MinValue
        }, default);

        attribute.Should().BeNull();
    }

    [Fact]
    public async Task GetAttribute_Should_Work_Without_EntityId()
    {
        var providers = CreateProviders();

        var snapToken = await providers.writer.Write([], [
            new AttributeTuple("workspace", "public", "status", JsonValue.Create("active")!)
        ], default);

        var attribute = await providers.reader.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "workspace",
            Attribute = "status",
            SnapToken = snapToken
        }, default);

        new
        {
            attribute!.EntityType,
            attribute.EntityId,
            attribute.Attribute,
            Value = attribute.Value.ToJsonString()
        }.Should().BeEquivalentTo(new
        {
            EntityType = "workspace",
            EntityId = "public",
            Attribute = "status",
            Value = JsonValue.Create("active")!.ToJsonString()
        });
    }

    [Fact]
    public async Task GetAttribute_Should_Respect_SnapToken_Without_EntityId()
    {
        var providers = CreateProviders();

        var firstSnapToken = await providers.writer.Write([], [
            new AttributeTuple("workspace", "public", "status", JsonValue.Create("active")!)
        ], default);

        var secondSnapToken = await providers.writer.Write([], [
            new AttributeTuple("workspace", "private", "status", JsonValue.Create("inactive")!)
        ], default);

        var attributeWithOldToken = await providers.reader.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "workspace",
            Attribute = "status",
            SnapToken = firstSnapToken
        }, default);

        var attributeWithNewToken = await providers.reader.GetAttribute(new EntityAttributeFilter
        {
            EntityType = "workspace",
            Attribute = "status",
            SnapToken = secondSnapToken
        }, default);

        attributeWithOldToken.Should().NotBeNull();
        attributeWithNewToken.Should().NotBeNull();
        attributeWithOldToken!.EntityId.Should().Be("public");
        attributeWithNewToken!.Attribute.Should().Be("status");
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
