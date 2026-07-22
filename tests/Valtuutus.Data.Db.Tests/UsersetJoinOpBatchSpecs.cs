using System.Data.Common;
using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

// Covers UsersetJoinOp's IBatchableCheckOp side (AddToBatch / ReadResultAsync) — the
// Execute-based ICheckOp path is already covered by UsersetJoinOpSpecs.
public class UsersetJoinOpBatchSpecs
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
        var op = new UsersetJoinOp("owner", "group", "member");
        var ops = Substitute.For<IRelationalBatchOps>();
        var batch = Substitute.For<DbBatch>();
        var ctx = Ctx();

        op.AddToBatch(batch, ops, ctx, "folder", "1");

        ops.Received(1).AddHasUsersetJoinRelationToBatch(
            batch, "folder", "1", "owner", "group", "member", "user", "u1", ctx.SnapToken);
    }

    [Fact]
    public async Task ReadResultAsync_reports_true_only_when_a_row_comes_back_true()
    {
        var op = new UsersetJoinOp("owner", "group", "member");
        var sink = new RecordingSink();

        await op.ReadResultAsync(new FakeBoolResultReader(true), token: 7, sink, default);
        await op.ReadResultAsync(new FakeBoolResultReader(false), token: 9, sink, default);
        await op.ReadResultAsync(new FakeBoolResultReader(null), token: 11, sink, default); // no rows

        sink.Completed.Should().ContainSingle(c => c.Token == 7 && c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 9 && !c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 11 && !c.Result);
    }
}
