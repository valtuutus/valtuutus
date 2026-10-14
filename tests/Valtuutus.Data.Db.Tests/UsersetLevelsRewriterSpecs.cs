using System.Data.Common;
using AwesomeAssertions;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupSubject.V2;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

public class UsersetLevelsRewriterSpecs
{
    private const string Schema = """
        entity user {}
        entity team {
            relation member @user;
        }
        entity group {
            relation member @user @group#member;
        }
        entity project {
            relation reviewer @team#member;
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

    private sealed class NoBatchOps : ILookupSubjectBatchOps
    {
        public void AddToBatch(DbBatch batch, LookupSubjectQuery query, SnapToken? snapToken) =>
            throw new NotSupportedException();
    }

    private static (LookupPlan Compiled, LookupPlan Rewritten) Rewrite(string entityType, string name, int hops,
        bool withBatchOps = true)
    {
        var schema = RelationalPlanRewriterSpecs.Parse(Schema);
        var compiled = new LookupSubjectPlanCompiler(schema, "user").Compile(entityType, name);
        var rewriter = new UsersetLevelsRewriter(new Options(hops), withBatchOps ? new NoBatchOps() : null);
        return (compiled, rewriter.Rewrite(compiled, schema, entityType, name, "user"));
    }

    [Fact]
    public void Non_terminal_userset_relation_becomes_userset_levels()
    {
        var (compiled, rewritten) = Rewrite("project", "viewer", hops: 3);

        var plan = rewritten.Should().BeOfType<UsersetLevelsPlan>().Subject;
        plan.Levels.Should().Be(3);
        plan.OneLevel.Should().BeSameAs(compiled);
        plan.Template.Kind.Should().Be(LookupSubjectQueryKind.UsersetLevels);
        plan.Template.EntityType.Should().Be("project");
        plan.Template.Relation.Should().Be("viewer");
        plan.Template.SubjectType.Should().Be("user");
    }

    [Fact]
    public void One_hop_per_query_changes_nothing()
    {
        var (compiled, rewritten) = Rewrite("project", "viewer", hops: 1);

        rewritten.Should().BeSameAs(compiled);
    }

    [Fact]
    public void Terminal_userset_relation_is_left_alone()
    {
        var (compiled, rewritten) = Rewrite("project", "reviewer", hops: 3);

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
        var (compiled, rewritten) = Rewrite("project", "viewer", hops: 3, withBatchOps: false);

        rewritten.Should().BeSameAs(compiled);
    }
}
