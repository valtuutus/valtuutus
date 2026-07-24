using System.Collections.Immutable;
using System.Data.Common;
using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

// Covers FusedExpressionOp's IBatchableCheckOp side (AddToBatch / ReadResultAsync) — the
// Execute-based ICheckOp path is already covered by FusedExpressionOpSpecs.
public class FusedExpressionOpBatchSpecs
{
    private static CheckRequestContext Ctx() => new()
    {
        SubjectType = "user", SubjectId = "u1",
        SnapToken = new SnapToken("00000000000000000000000001"),
        Context = new Dictionary<string, object>()
    };

    private static readonly ImmutableArray<FusedCheckLeaf> Leaves =
    [
        new FusedCheckLeaf(FusedLeafKind.TupleToUserSet, Negate: false, ["org"], TtuSubEntityType: "organization", TtuComputedRelation: "admin"),
        new FusedCheckLeaf(FusedLeafKind.Direct, Negate: false, ["owner"]),
    ];

    [Fact]
    public void AddToBatch_delegates_to_the_matching_IRelationalBatchOps_method_with_its_own_arguments()
    {
        var op = new FusedExpressionOp(Leaves, requireAll: false);
        var ops = Substitute.For<IRelationalBatchOps>();
        var batch = Substitute.For<DbBatch>();
        var ctx = Ctx();

        op.AddToBatch(batch, ops, ctx, "team", "1");

        ops.Received(1).AddHasFusedExpressionToBatch(
            batch, "team", "1", Leaves, false, "user", "u1", ctx.SnapToken);
    }

    [Fact]
    public async Task ReadResultAsync_reports_true_only_when_a_row_comes_back_true()
    {
        var op = new FusedExpressionOp(Leaves, requireAll: false);
        var sink = new RecordingSink();

        await op.ReadResultAsync(new FakeBoolResultReader(true), token: 7, sink, default);
        await op.ReadResultAsync(new FakeBoolResultReader(false), token: 9, sink, default);
        await op.ReadResultAsync(new FakeBoolResultReader(null), token: 11, sink, default); // no rows

        sink.Completed.Should().ContainSingle(c => c.Token == 7 && c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 9 && !c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 11 && !c.Result);
    }
}
