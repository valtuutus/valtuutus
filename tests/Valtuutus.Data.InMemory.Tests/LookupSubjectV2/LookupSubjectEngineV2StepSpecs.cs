using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Engines.LookupSubject.V2;

namespace Valtuutus.Data.InMemory.Tests.LookupSubjectV2;

public class LookupSubjectEngineV2StepSpecs
{
    private const string Schema = """
        entity user {}
        entity service {}
        entity organization {
            relation admin @user;
        }
        entity team {
            relation member @user;
        }
        entity group {
            relation member @user @group#member;
            relation teams @team;
            permission members := teams.member;
        }
        entity workspace {
            relation groups @group;
            permission view := groups.members;
        }
        entity project {
            relation org @organization;
            relation owner @user;
            relation editor @user;
            relation bot @service;
            relation reviewer @team#member;
            relation viewer @group#member;
            permission edit := owner or editor;
            permission review := reviewer;
            permission view := viewer;
            permission manage := org.admin;
            permission automate := bot or owner;
        }
        """;

    private sealed class RecordingRunner(ILookupSubjectQueryRunner inner) : ILookupSubjectQueryRunner
    {
        public List<(LookupSubjectQuery[] Queries, SnapToken? SnapToken)> Steps { get; } = [];

        public ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupSubjectQuery> queries, SnapToken? snapToken,
            CancellationToken cancellationToken)
        {
            Steps.Add((queries.ToArray(), snapToken));
            return inner.RunAsync(queries, snapToken, cancellationToken);
        }
    }

    private static async Task<(ILookupSubjectEngine Engine, RecordingRunner Runner, SnapToken Written)> Setup(
        params RelationTuple[] tuples)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddInMemory();
        services.AddValtuutusLookupSubjectV2();
        RecordingRunner? runner = null;
        services.Replace(ServiceDescriptor.Scoped<ILookupSubjectQueryRunner>(sp =>
            runner = new RecordingRunner(new DefaultLookupSubjectQueryRunner(sp.GetRequiredService<IDataReaderProvider>()))));
        var provider = services.BuildServiceProvider();
        var written = await provider.GetRequiredService<IDataWriterProvider>().Write(tuples, [], default);
        var engine = provider.GetRequiredService<ILookupSubjectEngine>();
        return (engine, runner!, written);
    }

    private static readonly RelationTuple[] NestedGroupTuples =
    [
        new("project", "p1", "viewer", "group", "g1", "member"),
        new("group", "g1", "member", "user", "u1"),
        new("group", "g1", "member", "group", "g2", "member"),
        new("group", "g2", "member", "user", "u2"),
    ];

    [Fact]
    public async Task Sibling_direct_relations_share_one_query()
    {
        var (engine, runner, _) = await Setup(
            new RelationTuple("project", "p1", "owner", "user", "u1"),
            new RelationTuple("project", "p1", "editor", "user", "u2"));

        var result = await engine.Lookup(new LookupSubjectRequest("project", "edit", "user", "p1"), default);

        result.Should().BeEquivalentTo("u1", "u2");
        var query = runner.Steps.Should().ContainSingle().Which.Queries.Should().ContainSingle().Subject;
        query.Kind.Should().Be(LookupSubjectQueryKind.RelationsMulti);
        query.Names.Should().BeEquivalentTo("owner", "editor");
    }

    [Fact]
    public async Task Terminal_userset_is_one_joined_query()
    {
        var (engine, runner, _) = await Setup(
            new RelationTuple("project", "p1", "reviewer", "team", "t1", "member"),
            new RelationTuple("team", "t1", "member", "user", "u1"));

        var result = await engine.Lookup(new LookupSubjectRequest("project", "review", "user", "p1"), default);

        result.Should().BeEquivalentTo("u1");
        var query = runner.Steps.Should().ContainSingle().Which.Queries.Should().ContainSingle().Subject;
        query.Kind.Should().Be(LookupSubjectQueryKind.Joined);
        query.Relation.Should().Be("reviewer");
        query.SubEntityType.Should().Be("team");
        query.SubjectRelation.Should().Be("member");
        query.Ids.Should().BeEquivalentTo("p1");
    }

    [Fact]
    public async Task Tuple_to_userset_with_terminal_target_is_one_joined_query()
    {
        var (engine, runner, _) = await Setup(
            new RelationTuple("project", "p1", "org", "organization", "o1"),
            new RelationTuple("organization", "o1", "admin", "user", "u1"));

        var result = await engine.Lookup(new LookupSubjectRequest("project", "manage", "user", "p1"), default);

        result.Should().BeEquivalentTo("u1");
        var query = runner.Steps.Should().ContainSingle().Which.Queries.Should().ContainSingle().Subject;
        query.Kind.Should().Be(LookupSubjectQueryKind.Joined);
        query.Relation.Should().Be("org");
        query.SubEntityType.Should().Be("organization");
        query.SubjectRelation.Should().BeEmpty();
        query.SubRelation.Should().Be("admin");
        query.SubjectType.Should().Be("user");
    }

    [Fact]
    public async Task Non_terminal_userset_walks_one_hop_per_step()
    {
        var (engine, runner, _) = await Setup(NestedGroupTuples);

        var result = await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1"), default);

        result.Should().BeEquivalentTo("u1", "u2");
        runner.Steps.Should().HaveCount(3);
        var first = runner.Steps[0].Queries.Should().ContainSingle().Subject;
        first.Kind.Should().Be(LookupSubjectQueryKind.Relations);
        first.EntityType.Should().Be("project");
        first.Relation.Should().Be("viewer");
        runner.Steps[1].Queries.Should().OnlyContain(q => q.EntityType == "group" && q.Ids.SequenceEqual(new[] { "g1" }));
        runner.Steps[2].Queries.Should().OnlyContain(q => q.EntityType == "group" && q.Ids.SequenceEqual(new[] { "g2" }));
    }

    [Fact]
    public async Task Converging_intermediate_entities_are_forwarded_once()
    {
        var (engine, runner, _) = await Setup(
            new RelationTuple("workspace", "w1", "groups", "group", "g1"),
            new RelationTuple("workspace", "w1", "groups", "group", "g2"),
            new RelationTuple("group", "g1", "teams", "team", "t1"),
            new RelationTuple("group", "g2", "teams", "team", "t1"),
            new RelationTuple("team", "t1", "member", "user", "alice"));

        var result = await engine.Lookup(new LookupSubjectRequest("workspace", "view", "user", "w1"), default);

        result.Should().BeEquivalentTo("alice");
        var queries = runner.Steps.SelectMany(s => s.Queries).ToList();
        queries.Should().OnlyContain(q => q.Ids.Distinct().Count() == q.Ids.Count);
        queries.Should().Contain(q => q.Ids.Count == 2 && q.Ids.Contains("g1") && q.Ids.Contains("g2"));
    }

    [Fact]
    public async Task Statically_unreachable_branch_issues_no_query()
    {
        var (engine, runner, _) = await Setup(
            new RelationTuple("project", "p1", "owner", "user", "u1"),
            new RelationTuple("project", "p1", "bot", "service", "s1"));

        var result = await engine.Lookup(new LookupSubjectRequest("project", "automate", "user", "p1"), default);

        result.Should().BeEquivalentTo("u1");
        var query = runner.Steps.Should().ContainSingle().Which.Queries.Should().ContainSingle().Subject;
        query.Kind.Should().Be(LookupSubjectQueryKind.Relations);
        query.Relation.Should().Be("owner");
    }

    [Fact]
    public async Task Unreachable_subject_type_runs_no_step()
    {
        var (engine, runner, _) = await Setup(new RelationTuple("project", "p1", "owner", "user", "u1"));

        var result = await engine.Lookup(new LookupSubjectRequest("project", "edit", "service", "p1"), default);

        result.Should().BeEmpty();
        runner.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task Spent_depth_skips_the_hop_query()
    {
        var (engine, runner, _) = await Setup(
            new RelationTuple("project", "p1", "org", "organization", "o1"),
            new RelationTuple("organization", "o1", "admin", "user", "u1"));

        var result = await engine.Lookup(new LookupSubjectRequest("project", "manage", "user", "p1", depth: 1), default);

        result.Should().BeEmpty();
        runner.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task Spent_depth_skips_the_userset_hop_query()
    {
        var (engine, runner, _) = await Setup(NestedGroupTuples);

        var result = await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1", depth: 2), default);

        result.Should().BeEmpty();
        runner.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task Tokenless_lookup_resolves_the_snap_token_once_and_pins_it()
    {
        var (engine, runner, written) = await Setup(NestedGroupTuples);

        await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1"), default);

        runner.Steps.Should().HaveCountGreaterThan(1);
        runner.Steps[0].SnapToken.Should().BeNull();
        runner.Steps.Skip(1).Should().OnlyContain(s => s.SnapToken == written);
    }

    [Fact]
    public async Task Explicit_snap_token_reaches_every_step()
    {
        var (engine, runner, written) = await Setup(NestedGroupTuples);

        await engine.Lookup(new LookupSubjectRequest("project", "view", "user", "p1") { SnapToken = written }, default);

        runner.Steps.Should().OnlyContain(s => s.SnapToken == written);
    }
}
