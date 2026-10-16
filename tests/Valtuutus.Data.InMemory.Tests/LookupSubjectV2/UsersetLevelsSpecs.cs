using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupSubject;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.InMemory.Tests.LookupSubjectV2;

public class UsersetLevelsSpecs
{
    private const string Schema = """
        entity user {}
        entity team {
            relation member @user;
        }
        entity group {
            relation member @user @group#member @team#member;
        }
        entity project {
            relation viewer @group#member;
        }
        """;

    // g0 -> g1 -> g2 -> g3, one user per group.
    private static readonly RelationTuple[] Chain =
    [
        new("group", "g0", "member", "user", "u0"),
        new("group", "g0", "member", "group", "g1", "member"),
        new("group", "g1", "member", "user", "u1"),
        new("group", "g1", "member", "group", "g2", "member"),
        new("group", "g2", "member", "user", "u2"),
        new("group", "g2", "member", "group", "g3", "member"),
        new("group", "g3", "member", "user", "u3"),
    ];

    // Same rule the provider rewriter applies: a direct relation with a userset that is not terminal.
    private sealed class LevelsRewriter(int levels) : ILookupSubjectPlanRewriter
    {
        public LookupPlan Rewrite(LookupPlan root, Schema schema, string entityType, string name, string subjectType) =>
            root is SetPlan set
            && schema.GetRelationType(entityType, name) == RelationType.DirectRelation
            && set.Children.Any(c => c is ExpandPlan)
                ? new UsersetLevelsPlan(new LookupSubjectQuery
                    {
                        Kind = LookupSubjectQueryKind.UsersetLevels, EntityType = entityType, Relation = name,
                        SubjectType = subjectType,
                    }, levels, root)
                : root;
    }

    // Answers UsersetLevels from the seeded tuples; everything else goes to the default runner.
    private sealed class LevelsRunner(ILookupSubjectQueryRunner inner, RelationTuple[] tuples) : ILookupSubjectQueryRunner
    {
        public List<LookupSubjectQuery[]> Steps { get; } = [];

        public async ValueTask<SnapToken> RunAsync(IReadOnlyList<LookupSubjectQuery> queries, SnapToken? snapToken,
            CancellationToken cancellationToken)
        {
            Steps.Add(queries.ToArray());
            var others = queries.Where(q => q.Kind != LookupSubjectQueryKind.UsersetLevels).ToList();
            var token = await inner.RunAsync(others, snapToken, cancellationToken);
            foreach (var query in queries.Where(q => q.Kind == LookupSubjectQueryKind.UsersetLevels))
                query.Result = Answer(query);
            return token;
        }

        private List<RelationTuple> Answer(LookupSubjectQuery query)
        {
            var answer = new List<RelationTuple>();
            var level = tuples.Where(t => t.EntityType == query.EntityType && t.Relation == query.Relation
                && query.Ids.Contains(t.EntityId)).ToList();
            for (var i = 0; i < query.Levels; i++)
            {
                var last = i == query.Levels - 1;
                answer.AddRange(level.Where(t => (t.SubjectRelation == "" && t.SubjectType == query.SubjectType)
                    || (last && t.SubjectRelation != "")));
                if (last) break;
                var next = level.Where(t => t.SubjectRelation != "")
                    .Select(t => (t.SubjectType, t.SubjectId, t.SubjectRelation)).ToHashSet();
                level = tuples.Where(t => next.Contains((t.EntityType, t.EntityId, t.Relation))).ToList();
            }
            return answer;
        }
    }

    private static async Task<(ILookupSubjectEngine Engine, LevelsRunner Runner)> Setup(int? levels,
        params RelationTuple[] tuples)
    {
        var services = new ServiceCollection().AddValtuutusCore(Schema);
        services.AddInMemory();
        if (levels is { } k)
            services.AddSingleton<ILookupSubjectPlanRewriter>(new LevelsRewriter(k));
        LevelsRunner? runner = null;
        services.Replace(ServiceDescriptor.Scoped<ILookupSubjectQueryRunner>(sp =>
            runner = new LevelsRunner(new DefaultLookupSubjectQueryRunner(sp.GetRequiredService<IDataReaderProvider>()),
                tuples)));
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IDataWriterProvider>().Write(tuples, [], default);
        var engine = provider.GetRequiredService<ILookupSubjectEngine>();
        return (engine, runner!);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 1)]
    public async Task Four_nested_levels_take_fewer_steps(int levels, int steps)
    {
        var (engine, runner) = await Setup(levels, Chain);

        var result = await engine.Lookup(new LookupSubjectRequest("group", "member", "user", "g0"), default);

        result.Should().BeEquivalentTo("u0", "u1", "u2", "u3");
        runner.Steps.Should().HaveCount(steps);
        runner.Steps.Should().OnlyContain(step => step.All(q => q.Kind == LookupSubjectQueryKind.UsersetLevels));
        runner.Steps[0].Should().ContainSingle().Which.Levels.Should().Be(levels);
    }

    [Fact]
    public async Task Continuation_starts_from_the_last_levels_usersets()
    {
        var (engine, runner) = await Setup(2, Chain);

        await engine.Lookup(new LookupSubjectRequest("group", "member", "user", "g0"), default);

        runner.Steps[1].Should().ContainSingle().Which.Ids.Should().BeEquivalentTo("g2");
    }

    [Fact]
    public async Task One_level_of_depth_runs_the_relation_plan()
    {
        var (engine, runner) = await Setup(3, Chain);

        var result = await engine.Lookup(new LookupSubjectRequest("group", "member", "user", "g0", depth: 1), default);

        result.Should().BeEquivalentTo("u0");
        runner.Steps.Should().ContainSingle().Which.Should().OnlyContain(q => q.Kind != LookupSubjectQueryKind.UsersetLevels);
    }

    [Fact]
    public async Task Cycle_in_the_data_ends_once_depth_is_spent()
    {
        var (engine, _) = await Setup(3,
            new RelationTuple("group", "g1", "member", "user", "u1"),
            new RelationTuple("group", "g1", "member", "group", "g2", "member"),
            new RelationTuple("group", "g2", "member", "user", "u2"),
            new RelationTuple("group", "g2", "member", "group", "g1", "member"));

        var result = await engine.Lookup(new LookupSubjectRequest("group", "member", "user", "g1"), default);

        result.Should().BeEquivalentTo("u1", "u2");
    }

    private static readonly RelationTuple[] MixedChain =
    [
        new("project", "p1", "viewer", "group", "g1", "member"),
        new("group", "g1", "member", "user", "u1"),
        new("group", "g1", "member", "group", "g2", "member"),
        new("group", "g2", "member", "user", "u2"),
        new("group", "g2", "member", "team", "t1", "member"),
        new("team", "t1", "member", "user", "u3"),
        new("group", "g2", "member", "group", "g3", "member"),
        new("group", "g3", "member", "user", "u4"),
    ];

    public static TheoryData<int, int> LevelsAndDepths()
    {
        var data = new TheoryData<int, int>();
        foreach (var levels in new[] { 2, 3, 4 })
            for (var depth = 1; depth <= 6; depth++)
                data.Add(levels, depth);
        return data;
    }

    [Theory]
    [MemberData(nameof(LevelsAndDepths))]
    public async Task Fused_lookup_grants_what_one_level_per_step_grants(int levels, int depth)
    {
        var (fused, _) = await Setup(levels, MixedChain);
        var (plain, _) = await Setup(null, MixedChain);
        var request = new LookupSubjectRequest("project", "viewer", "user", "p1", depth);

        var expected = await plain.Lookup(request, default);
        var actual = await fused.Lookup(request, default);

        actual.Should().BeEquivalentTo(expected);
    }
}
