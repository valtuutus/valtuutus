using System.Data.Common;
using AwesomeAssertions;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

public class FixpointLevelsRewriterSpecs
{
    private const string Schema = """
        entity user {}
        entity group {
            relation member @user @group#member;
        }
        entity project {
            relation viewer @group#member;
            relation owner @user;
            permission view := viewer or owner;
        }
        """;

    private sealed record Options(int LookupHopsPerQuery) : IValtuutusDbOptions
    {
        public string Schema => "public";
        public string TransactionsTableName => "transactions";
        public string RelationsTableName => "relation_tuples";
        public string AttributesTableName => "attributes";
    }

    private sealed class NoBatchOps : ILookupEntityBatchOps
    {
        public void AddToBatch(DbBatch batch, LookupEntityQuery query, SnapToken? snapToken) =>
            throw new NotSupportedException();
    }

    private static (LookupEntityPlan Compiled, LookupEntityPlan Rewritten) Rewrite(string entityType, string name,
        int hops, bool withBatchOps = true)
    {
        var schema = RelationalPlanRewriterSpecs.Parse(Schema);
        var compiled = new LookupEntityPlanCompiler(schema, "user").Compile(entityType, name);
        var rewriter = new FixpointLevelsRewriter(new Options(hops), withBatchOps ? new NoBatchOps() : null);
        return (compiled, rewriter.Rewrite(compiled, schema, entityType, name, "user"));
    }

    [Fact]
    public void Self_recursive_relation_walks_several_hops_per_query()
    {
        var (compiled, rewritten) = Rewrite("group", "member", hops: 3);

        var plan = rewritten.Should().BeOfType<FixpointLevelsPlan>().Subject;
        plan.Levels.Should().Be(3);
        plan.OneLevel.Should().BeSameAs(compiled);
        plan.Template.Kind.Should().Be(LookupEntityQueryKind.RelationsLevels);
        plan.Template.EntityType.Should().Be("group");
        plan.Template.Relation.Should().Be("member");
    }

    [Fact]
    public void One_hop_per_query_changes_nothing()
    {
        var (compiled, rewritten) = Rewrite("group", "member", hops: 1);

        rewritten.Should().BeSameAs(compiled);
    }

    [Fact]
    public void Userset_into_another_relation_is_left_alone()
    {
        var (compiled, rewritten) = Rewrite("project", "viewer", hops: 3);

        rewritten.Should().BeSameAs(compiled);
    }

    [Fact]
    public void Permission_is_left_alone()
    {
        var (compiled, rewritten) = Rewrite("project", "view", hops: 3);

        rewritten.Should().BeSameAs(compiled);
    }

    [Fact]
    public void Provider_without_lookup_batch_ops_changes_nothing()
    {
        var (compiled, rewritten) = Rewrite("group", "member", hops: 3, withBatchOps: false);

        rewritten.Should().BeSameAs(compiled);
    }
}
