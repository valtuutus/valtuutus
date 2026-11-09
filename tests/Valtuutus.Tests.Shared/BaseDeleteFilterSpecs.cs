using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Data;

namespace Valtuutus.Tests.Shared;

/// <summary>
/// A relation delete filter matches the live tuples equal to it on every field it sets; fields left
/// null match anything. Several filters in one delete remove the union of their matches.
/// </summary>
public abstract class BaseDeleteFilterSpecs : IAsyncLifetime
{
    private static readonly RelationTuple OwnerP1 = new("project", "p1", "owner", "user", "u1");
    private static readonly RelationTuple MemberP1 = new("project", "p1", "member", "user", "u2");
    private static readonly RelationTuple MemberP2 = new("project", "p2", "member", "user", "u1");
    private static readonly RelationTuple GroupMemberP2 = new("project", "p2", "member", "group", "g1", "member");
    private static readonly RelationTuple OwnerP3 = new("project", "p3", "owner", "user", "u3");
    private static readonly RelationTuple MemberG1 = new("group", "g1", "member", "user", "u1");
    private static readonly RelationTuple GroupOwnerP1 = new("project", "p1", "owner", "group", "g1", "member");

    private static readonly RelationTuple[] All =
        [OwnerP1, MemberP1, MemberP2, GroupMemberP2, OwnerP3, MemberG1, GroupOwnerP1];

    private ServiceProvider _provider = default!;

    protected BaseDeleteFilterSpecs(IDatabaseFixture fixture) => Fixture = fixture;

    protected IDatabaseFixture Fixture { get; }

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);

    public async Task InitializeAsync()
    {
        await Fixture.ResetDatabaseAsync();
        var services = new ServiceCollection().AddValtuutusCore(TestsConsts.DefaultSchema);
        AddSpecificProvider(services);
        _provider = services.BuildServiceProvider();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    public static TheoryData<int> EveryFieldCombination()
    {
        var data = new TheoryData<int>();
        for (var fields = 0; fields < 64; fields++)
            data.Add(fields);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFieldCombination))]
    public async Task A_filter_deletes_the_tuples_equal_to_it_on_the_fields_it_sets(int fields)
    {
        // Bits, lowest first: entity type, entity id, relation, subject type, subject id, subject relation.
        bool Sets(int bit) => (fields & (1 << bit)) != 0;
        var filter = new DeleteRelationsFilter
        {
            EntityType = Sets(0) ? OwnerP1.EntityType : null,
            EntityId = Sets(1) ? OwnerP1.EntityId : null,
            Relation = Sets(2) ? OwnerP1.Relation : null,
            SubjectType = Sets(3) ? OwnerP1.SubjectType : null,
            SubjectId = Sets(4) ? OwnerP1.SubjectId : null,
            SubjectRelation = Sets(5) ? OwnerP1.SubjectRelation : null,
        };

        var token = await Delete(filter);

        (await LiveRelations(token)).Should().BeEquivalentTo(All.Where(t => !Matches(filter, t)));
    }

    [Fact]
    public async Task Filters_setting_different_fields_delete_exactly_their_matches()
    {
        var token = await Delete(
            new DeleteRelationsFilter
            {
                EntityType = "project", EntityId = "p1", Relation = "owner",
                SubjectType = "user", SubjectId = "u1", SubjectRelation = ""
            },
            new DeleteRelationsFilter { EntityType = "project", EntityId = "p3" },
            new DeleteRelationsFilter { SubjectType = "user", SubjectId = "u2" },
            new DeleteRelationsFilter { EntityType = "group", EntityId = "g1", Relation = "member" });

        (await LiveRelations(token)).Should().BeEquivalentTo([MemberP2, GroupMemberP2, GroupOwnerP1]);
    }

    [Fact]
    public async Task Filters_setting_the_same_fields_are_applied_together()
    {
        var token = await Delete(
            new DeleteRelationsFilter { EntityType = "project", EntityId = "p1" },
            new DeleteRelationsFilter { EntityType = "project", EntityId = "p3" });

        (await LiveRelations(token)).Should().BeEquivalentTo([MemberP2, GroupMemberP2, MemberG1]);
    }

    [Fact]
    public async Task A_subject_filter_deletes_every_tuple_of_that_subject()
    {
        var token = await Delete(new DeleteRelationsFilter { SubjectType = "user", SubjectId = "u1" });

        (await LiveRelations(token)).Should().BeEquivalentTo([MemberP1, GroupMemberP2, OwnerP3, GroupOwnerP1]);
    }

    [Fact]
    public async Task An_empty_subject_relation_matches_only_direct_tuples()
    {
        var token = await Delete(new DeleteRelationsFilter { EntityType = "project", EntityId = "p2", SubjectRelation = "" });

        (await LiveRelations(token)).Should().BeEquivalentTo([OwnerP1, MemberP1, GroupMemberP2, OwnerP3, MemberG1, GroupOwnerP1]);
    }

    [Fact]
    public async Task A_filter_setting_no_field_deletes_every_live_tuple()
    {
        var token = await Delete(new DeleteRelationsFilter());

        (await LiveRelations(token)).Should().BeEmpty();
    }

    private static bool Matches(DeleteRelationsFilter f, RelationTuple t) =>
        (f.EntityType is null || f.EntityType == t.EntityType)
        && (f.EntityId is null || f.EntityId == t.EntityId)
        && (f.Relation is null || f.Relation == t.Relation)
        && (f.SubjectType is null || f.SubjectType == t.SubjectType)
        && (f.SubjectId is null || f.SubjectId == t.SubjectId)
        && (f.SubjectRelation is null || f.SubjectRelation == t.SubjectRelation);

    private async Task<SnapToken> Delete(params DeleteRelationsFilter[] filters)
    {
        var writer = _provider.GetRequiredService<IDataWriterProvider>();
        await writer.Write(All, [], default);
        return await writer.Delete(new DeleteFilter { Relations = filters }, default);
    }

    // Reads every entity and relation the seed uses, at the delete's token.
    private async Task<List<RelationTuple>> LiveRelations(SnapToken token)
    {
        var reader = _provider.GetRequiredService<IDataReaderProvider>();
        var live = new List<RelationTuple>();
        foreach (var (type, id, relation) in All.Select(t => (t.EntityType, t.EntityId, t.Relation)).Distinct())
        {
            using var relations = await reader.GetRelations(new RelationTupleFilter
            {
                EntityType = type, EntityId = id, Relation = relation, SnapToken = token
            }, default);
            live.AddRange(relations);
        }
        return live;
    }
}
