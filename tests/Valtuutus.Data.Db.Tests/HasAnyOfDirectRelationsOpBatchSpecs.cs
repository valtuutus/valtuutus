using System.Data.Common;
using AwesomeAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

// Covers HasAnyOfDirectRelationsOp's IBatchableCheckOp side (AddToBatch / ReadResultAsync) — the
// Execute-based ICheckOp path is already covered by HasAnyOfDirectRelationsOpSpecs.
public class HasAnyOfDirectRelationsOpBatchSpecs
{
    private static CheckRequestContext Ctx() => new()
    {
        SubjectType = "user", SubjectId = "u1",
        SnapToken = new SnapToken("00000000000000000000000001"),
        Context = new Dictionary<string, object>()
    };

    [Fact]
    public void AddToBatch_delegates_to_the_matching_IRelationalBatchOps_method_with_its_own_arguments()
    {
        var op = new HasAnyOfDirectRelationsOp(["owner", "admin"], requireAll: false);
        var ops = Substitute.For<IRelationalBatchOps>();
        var batch = Substitute.For<DbBatch>();
        var ctx = Ctx();

        op.AddToBatch(batch, ops, ctx, "household", "1");

        ops.Received(1).AddHasAnyOfDirectRelationsToBatch(
            batch, "household", "1", Arg.Is<string[]>(r => r.SequenceEqual(new[] { "owner", "admin" })),
            "u1", ctx.SnapToken);
    }

    [Fact]
    public async Task ReadResultAsync_any_mode_is_true_when_at_least_one_row_comes_back()
    {
        var op = new HasAnyOfDirectRelationsOp(["owner", "admin"], requireAll: false);
        var sink = new RecordingSink();

        using var reader1 = new FakeStringResultReader(["admin"]);
        await op.ReadResultAsync(reader1, token: 7, sink, default);
        using var reader2 = new FakeStringResultReader([]);
        await op.ReadResultAsync(reader2, token: 9, sink, default);

        sink.Completed.Should().ContainSingle(c => c.Token == 7 && c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 9 && !c.Result);
    }

    [Fact]
    public async Task ReadResultAsync_all_mode_requires_every_relation_to_come_back()
    {
        var op = new HasAnyOfDirectRelationsOp(["owner", "admin"], requireAll: true);
        var sink = new RecordingSink();

        using var reader3 = new FakeStringResultReader(["owner", "admin"]);
        await op.ReadResultAsync(reader3, token: 1, sink, default);
        using var reader4 = new FakeStringResultReader(["owner"]);
        await op.ReadResultAsync(reader4, token: 2, sink, default);

        sink.Completed.Should().ContainSingle(c => c.Token == 1 && c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 2 && !c.Result);
    }
}
