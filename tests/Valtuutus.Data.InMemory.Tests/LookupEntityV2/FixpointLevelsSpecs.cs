using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.InMemory.Tests.LookupEntityV2;

public class FixpointLevelsSpecs
{
    private const string Schema = """
        entity user {}
        entity organization {}
        entity team {
            relation member @user;
        }
        entity group {
            relation member @user @group#member @team#member;
            relation org @organization;
        }
        entity circle {
            relation member @user @circle#member;
            relation org @organization;
        }
        """;

    // u is a direct member of g3; g2 holds g3, g1 holds g2, g0 holds g1.
    private static readonly RelationTuple[] Chain =
    [
        new("group", "g3", "member", "user", "u"),
        new("group", "g2", "member", "group", "g3", "member"),
        new("group", "g1", "member", "group", "g2", "member"),
        new("group", "g0", "member", "group", "g1", "member"),
    ];

    // Same rule the provider rewriter applies: every fixpoint walks several hops per query, and a
    // walk with no joined grants starts from the subject.
    private sealed class LevelsRewriter(int levels) : ILookupEntityPlanRewriter
    {
        public LookupEntityPlan Rewrite(LookupEntityPlan root, Schema schema, string entityType, string name,
            string subjectType) =>
            root is FixpointPlan fixpoint
                ? new FixpointLevelsPlan(
                    new LookupEntityQuery
                    {
                        Kind = LookupEntityQueryKind.RelationsLevels, EntityType = entityType, Relation = name,
                    },
                    levels, fixpoint,
                    new LookupEntityQuery
                    {
                        Kind = LookupEntityQueryKind.RelationsLevelsFromSubject, EntityType = entityType,
                        Relation = name, SubjectType = subjectType,
                    })
                : root;
    }

    // Records every step and answers both levels kinds from the test's tuples; everything else goes
    // to the default runner.
    private sealed class LevelsRunner(ILookupEntityQueryRunner inner, RelationTuple[] tuples) : ILookupEntityQueryRunner
    {
        public List<LookupEntityQuery[]> Steps { get; } = [];

        private static bool IsLevels(LookupEntityQuery q) =>
            q.Kind is LookupEntityQueryKind.RelationsLevels or LookupEntityQueryKind.RelationsLevelsFromSubject;

        public async ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupEntityQuery> queries, SnapToken? snapToken,
            CancellationToken cancellationToken)
        {
            // Ids are readable only while the step runs, so each recorded query keeps a copy.
            Steps.Add(queries.Select(q => q.For(q.Ids.ToArray(), q.Scope, q.Levels, q.LevelOneIds.ToArray())).ToArray());
            var others = queries.Where(q => !IsLevels(q)).ToList();
            var token = await inner.RunAsync(others, snapToken, cancellationToken);
            foreach (var query in queries.Where(IsLevels))
                query.Result = Answer(query);
            return token;
        }

        private List<RelationTuple> Answer(LookupEntityQuery query)
        {
            bool IsHop(RelationTuple t) => t.EntityType == query.EntityType && t.Relation == query.Relation
                && t.SubjectType == query.EntityType && t.SubjectRelation == query.Relation;

            var fromSubject = query.Kind == LookupEntityQueryKind.RelationsLevelsFromSubject;
            var firstLevel = new Dictionary<string, int>();
            HashSet<string> level;
            if (fromSubject)
            {
                level = tuples
                    .Where(t => t.EntityType == query.EntityType && t.Relation == query.Relation
                        && t.SubjectType == query.SubjectType && t.SubjectRelation == ""
                        && query.Ids.Contains(t.SubjectId))
                    .Select(t => t.EntityId).ToHashSet();
            }
            else
            {
                level = query.LevelOneIds.ToHashSet();
                level.UnionWith(tuples.Where(t => IsHop(t) && query.Ids.Contains(t.SubjectId)).Select(t => t.EntityId));
            }
            for (var i = fromSubject ? 0 : 1; ; i++)
            {
                foreach (var id in level) firstLevel.TryAdd(id, i);
                if (i == query.Levels) break;
                var previous = level;
                level = tuples.Where(t => IsHop(t) && previous.Contains(t.SubjectId)).Select(t => t.EntityId).ToHashSet();
            }
            return firstLevel
                .Select(e => new RelationTuple(query.EntityType, e.Key, e.Value == query.Levels ? query.Relation : "", "", ""))
                .ToList();
        }
    }

    private static async Task<(ILookupEntityEngine Engine, LevelsRunner Runner)> Setup(int? levels,
        params RelationTuple[] tuples)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddInMemory();
        if (levels is { } k)
            services.AddSingleton<ILookupEntityPlanRewriter>(new LevelsRewriter(k));
        LevelsRunner? runner = null;
        services.Replace(ServiceDescriptor.Scoped<ILookupEntityQueryRunner>(sp =>
            runner = new LevelsRunner(new DefaultLookupEntityQueryRunner(sp.GetRequiredService<IDataReaderProvider>()),
                tuples)));
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDataWriterProvider>().Write(tuples, [], default);
        var engine = provider.GetRequiredService<ILookupEntityEngine>();
        return (engine, runner!);
    }

    // Steps: the direct read, then hop queries. k=2: g2 and g1, then g0 and an empty level.
    // k=3: g2, g1 and g0, then nothing new. k=4: g2, g1, g0 and an empty last level.
    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    public async Task Four_nested_levels_take_fewer_steps(int levels, int steps)
    {
        var (engine, runner) = await Setup(levels, Chain);

        var result = await engine.LookupEntity(new LookupEntityRequest("group", "member", "user", "u"), default);

        result.EntityIds.Should().Equal("g0", "g1", "g2", "g3");
        runner.Steps.Should().HaveCount(steps);
        var hop = runner.Steps[1].Should().ContainSingle().Subject;
        hop.Kind.Should().Be(LookupEntityQueryKind.RelationsLevels);
        hop.Levels.Should().Be(levels);
        hop.Ids.Should().BeEquivalentTo("g3");
    }

    [Fact]
    public async Task Walk_continues_from_the_entities_first_found_at_the_last_level()
    {
        var (engine, runner) = await Setup(2, Chain);

        await engine.LookupEntity(new LookupEntityRequest("group", "member", "user", "u"), default);

        runner.Steps[2].Should().ContainSingle().Which.Ids.Should().BeEquivalentTo("g1");
    }

    // Depth 4 leaves the walk three levels: one query of two, then the one-level hop.
    [Fact]
    public async Task Depth_left_for_one_level_runs_the_plain_hop()
    {
        var (engine, runner) = await Setup(2, Chain);

        var result = await engine.LookupEntity(new LookupEntityRequest("group", "member", "user", "u", depth: 4), default);

        result.EntityIds.Should().Equal("g0", "g1", "g2", "g3");
        runner.Steps.Should().HaveCount(3);
        runner.Steps[1].Should().ContainSingle().Which.Levels.Should().Be(2);
        var last = runner.Steps[2].Should().ContainSingle().Subject;
        last.Kind.Should().Be(LookupEntityQueryKind.Relations);
        last.Ids.Should().BeEquivalentTo("g1");
    }

    // A joined grant costs a level, so it enters the first hop query at level one.
    [Fact]
    public async Task Joined_grants_join_the_first_level()
    {
        var (engine, runner) = await Setup(2,
            new RelationTuple("team", "t1", "member", "user", "u"),
            new RelationTuple("group", "g2", "member", "team", "t1", "member"),
            new RelationTuple("group", "g1", "member", "group", "g2", "member"),
            new RelationTuple("group", "g0", "member", "group", "g1", "member"));

        var result = await engine.LookupEntity(new LookupEntityRequest("group", "member", "user", "u"), default);

        result.EntityIds.Should().Equal("g0", "g1", "g2");
        runner.Steps.Should().HaveCount(3);
        var hop = runner.Steps[1].Should().ContainSingle().Subject;
        hop.Ids.Should().BeEmpty();
        hop.LevelOneIds.Should().BeEquivalentTo("g2");
        runner.Steps[2].Should().ContainSingle().Which.LevelOneIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Cycle_in_the_data_stops_once_nothing_is_new()
    {
        var (engine, runner) = await Setup(3,
            new RelationTuple("group", "g1", "member", "user", "u"),
            new RelationTuple("group", "g2", "member", "group", "g1", "member"),
            new RelationTuple("group", "g1", "member", "group", "g2", "member"));

        var result = await engine.LookupEntity(new LookupEntityRequest("group", "member", "user", "u"), default);

        result.EntityIds.Should().Equal("g1", "g2");
        runner.Steps.Should().HaveCount(2);
    }

    // Steps: the direct reads (scoped and unscoped), the walk's one hop query (g1, then an empty
    // level), then the scoped root hop over the walk's groups.
    [Fact]
    public async Task Scoped_root_keeps_its_scoped_hop_over_the_fused_walk()
    {
        var (engine, runner) = await Setup(2,
            new RelationTuple("group", "g2", "member", "user", "u"),
            new RelationTuple("group", "g1", "member", "group", "g2", "member"),
            new RelationTuple("group", "g1", "org", "organization", "o1"));

        var result = await engine.LookupEntity(
            new LookupEntityRequest("group", "member", "user", "u") { Scope = new EntityScope("org", "organization", "o1") },
            default);

        result.EntityIds.Should().Equal("g1");
        runner.Steps.Should().HaveCount(3);
        runner.Steps[1].Should().ContainSingle().Which.Kind.Should().Be(LookupEntityQueryKind.RelationsLevels);
        var root = runner.Steps[2].Should().ContainSingle().Subject;
        root.Kind.Should().Be(LookupEntityQueryKind.Relations);
        root.Scope.Should().Be(new EntityScope("org", "organization", "o1"));
    }

    // g5 holds u directly, g4 through team t1 (one level); g3 holds g5, g2 holds g3, g1 holds g2 and
    // g4 (a shortcut), g0 holds g1, g2 holds g0 (a cycle), gx holds g0.
    private static readonly RelationTuple[] Mixed =
    [
        new("group", "g5", "member", "user", "u"),
        new("team", "t1", "member", "user", "u"),
        new("group", "g4", "member", "team", "t1", "member"),
        new("group", "g3", "member", "group", "g5", "member"),
        new("group", "g2", "member", "group", "g3", "member"),
        new("group", "g1", "member", "group", "g2", "member"),
        new("group", "g1", "member", "group", "g4", "member"),
        new("group", "g0", "member", "group", "g1", "member"),
        new("group", "g2", "member", "group", "g0", "member"),
        new("group", "gx", "member", "group", "g0", "member"),
    ];

    public static TheoryData<int, int> LevelsAndDepths()
    {
        var data = new TheoryData<int, int>();
        foreach (var levels in new[] { 2, 3, 4 })
            for (var depth = 1; depth <= 7; depth++)
                data.Add(levels, depth);
        return data;
    }

    [Theory]
    [MemberData(nameof(LevelsAndDepths))]
    public async Task Fused_walk_grants_what_one_hop_per_step_grants(int levels, int depth)
    {
        var (fused, _) = await Setup(levels, Mixed);
        var (plain, _) = await Setup(null, Mixed);
        var request = new LookupEntityRequest("group", "member", "user", "u", depth: depth);

        var expected = await plain.LookupEntity(request, default);
        var actual = await fused.LookupEntity(request, default);

        actual.EntityIds.Should().Equal(expected.EntityIds);
    }

    // u is a direct member of c3; c2 holds c3, c1 holds c2, c0 holds c1.
    private static readonly RelationTuple[] CircleChain =
    [
        new("circle", "c3", "member", "user", "u"),
        new("circle", "c2", "member", "circle", "c3", "member"),
        new("circle", "c1", "member", "circle", "c2", "member"),
        new("circle", "c0", "member", "circle", "c1", "member"),
    ];

    // The first query reads the direct grants as level 0, then k levels. k=2: c3, c2, c1, then c0
    // and an empty level. k=3: c3 to c0, then nothing new. k=4: c3 to c0 and an empty last level.
    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    public async Task Walk_without_joined_grants_reads_its_direct_grants_in_its_first_query(int levels, int steps)
    {
        var (engine, runner) = await Setup(levels, CircleChain);

        var result = await engine.LookupEntity(new LookupEntityRequest("circle", "member", "user", "u"), default);

        result.EntityIds.Should().Equal("c0", "c1", "c2", "c3");
        runner.Steps.Should().HaveCount(steps);
        var first = runner.Steps[0].Should().ContainSingle().Subject;
        first.Kind.Should().Be(LookupEntityQueryKind.RelationsLevelsFromSubject);
        first.Levels.Should().Be(levels);
        first.SubjectType.Should().Be("user");
        first.Ids.Should().BeEquivalentTo("u");
    }

    [Fact]
    public async Task Walk_continues_from_the_entities_the_first_query_found_at_its_last_level()
    {
        var (engine, runner) = await Setup(2, CircleChain);

        await engine.LookupEntity(new LookupEntityRequest("circle", "member", "user", "u"), default);

        var next = runner.Steps[1].Should().ContainSingle().Subject;
        next.Kind.Should().Be(LookupEntityQueryKind.RelationsLevels);
        next.Ids.Should().BeEquivalentTo("c1");
        next.LevelOneIds.Should().BeEmpty();
    }

    // Depth 2 leaves the walk one level: the direct read and one hop in one query.
    [Fact]
    public async Task Walk_with_one_level_left_reads_its_direct_grants_and_that_level_in_one_query()
    {
        var (engine, runner) = await Setup(2, CircleChain);

        var result = await engine.LookupEntity(new LookupEntityRequest("circle", "member", "user", "u", depth: 2), default);

        result.EntityIds.Should().Equal("c2", "c3");
        var only = runner.Steps.Should().ContainSingle().Subject.Should().ContainSingle().Subject;
        only.Kind.Should().Be(LookupEntityQueryKind.RelationsLevelsFromSubject);
        only.Levels.Should().Be(1);
    }

    // Depth 1 leaves the walk no level: the plain direct read.
    [Fact]
    public async Task Walk_with_no_level_left_runs_the_plain_direct_read()
    {
        var (engine, runner) = await Setup(2, CircleChain);

        var result = await engine.LookupEntity(new LookupEntityRequest("circle", "member", "user", "u", depth: 1), default);

        result.EntityIds.Should().Equal("c3");
        runner.Steps.Should().ContainSingle().Which.Should().ContainSingle()
            .Which.Kind.Should().Be(LookupEntityQueryKind.Relations);
    }

    // Depth 4 leaves the walk three levels: the direct read and two levels, then the one-level hop.
    [Fact]
    public async Task Depth_left_after_the_first_query_for_one_level_runs_the_plain_hop()
    {
        var (engine, runner) = await Setup(2, CircleChain);

        var result = await engine.LookupEntity(new LookupEntityRequest("circle", "member", "user", "u", depth: 4), default);

        result.EntityIds.Should().Equal("c0", "c1", "c2", "c3");
        runner.Steps.Should().HaveCount(2);
        var last = runner.Steps[1].Should().ContainSingle().Subject;
        last.Kind.Should().Be(LookupEntityQueryKind.Relations);
        last.Ids.Should().BeEquivalentTo("c1");
    }

    [Fact]
    public async Task Direct_grant_with_nothing_above_it_takes_one_step()
    {
        var (engine, runner) = await Setup(2, new RelationTuple("circle", "c3", "member", "user", "u"));

        var result = await engine.LookupEntity(new LookupEntityRequest("circle", "member", "user", "u"), default);

        result.EntityIds.Should().Equal("c3");
        runner.Steps.Should().ContainSingle();
    }

    [Fact]
    public async Task Cycle_found_by_the_first_query_stops_there()
    {
        var (engine, runner) = await Setup(3,
            new RelationTuple("circle", "c1", "member", "user", "u"),
            new RelationTuple("circle", "c2", "member", "circle", "c1", "member"),
            new RelationTuple("circle", "c1", "member", "circle", "c2", "member"));

        var result = await engine.LookupEntity(new LookupEntityRequest("circle", "member", "user", "u"), default);

        result.EntityIds.Should().Equal("c1", "c2");
        runner.Steps.Should().ContainSingle();
    }

    // Steps: the scoped direct read and the walk's first query together, then the scoped root hop
    // over the walk's circles.
    [Fact]
    public async Task Scoped_root_reads_its_scoped_direct_grants_beside_the_walks_first_query()
    {
        var (engine, runner) = await Setup(2,
            new RelationTuple("circle", "c2", "member", "user", "u"),
            new RelationTuple("circle", "c1", "member", "circle", "c2", "member"),
            new RelationTuple("circle", "c1", "org", "organization", "o1"));

        var result = await engine.LookupEntity(
            new LookupEntityRequest("circle", "member", "user", "u") { Scope = new EntityScope("org", "organization", "o1") },
            default);

        result.EntityIds.Should().Equal("c1");
        runner.Steps.Should().HaveCount(2);
        runner.Steps[0].Select(q => q.Kind).Should().BeEquivalentTo(
            [LookupEntityQueryKind.Relations, LookupEntityQueryKind.RelationsLevelsFromSubject]);
        runner.Steps[0].Single(q => q.Kind == LookupEntityQueryKind.Relations).Scope
            .Should().Be(new EntityScope("org", "organization", "o1"));
        var root = runner.Steps[1].Should().ContainSingle().Subject;
        root.Kind.Should().Be(LookupEntityQueryKind.Relations);
        root.Scope.Should().Be(new EntityScope("org", "organization", "o1"));
    }

    // c5 and c4 hold u directly; c3 holds c5, c2 holds c3, c1 holds c2 and c4 (a shortcut), c0 holds
    // c1, c2 holds c0 (a cycle), cx holds c0.
    private static readonly RelationTuple[] CircleMixed =
    [
        new("circle", "c5", "member", "user", "u"),
        new("circle", "c4", "member", "user", "u"),
        new("circle", "c3", "member", "circle", "c5", "member"),
        new("circle", "c2", "member", "circle", "c3", "member"),
        new("circle", "c1", "member", "circle", "c2", "member"),
        new("circle", "c1", "member", "circle", "c4", "member"),
        new("circle", "c0", "member", "circle", "c1", "member"),
        new("circle", "c2", "member", "circle", "c0", "member"),
        new("circle", "cx", "member", "circle", "c0", "member"),
    ];

    [Theory]
    [MemberData(nameof(LevelsAndDepths))]
    public async Task Walk_from_the_subject_grants_what_one_hop_per_step_grants(int levels, int depth)
    {
        var (fused, _) = await Setup(levels, CircleMixed);
        var (plain, _) = await Setup(null, CircleMixed);
        var request = new LookupEntityRequest("circle", "member", "user", "u", depth: depth);

        var expected = await plain.LookupEntity(request, default);
        var actual = await fused.LookupEntity(request, default);

        actual.EntityIds.Should().Equal(expected.EntityIds);
    }
}
