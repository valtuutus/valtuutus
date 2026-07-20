using System.Data.Common;
using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Db.Tests;

// Symmetric to HasAnyOfDirectRelationsOpBatchSpecs — covers HasAnyOfAttributesOp's
// IBatchableCheckOp side.
public class HasAnyOfAttributesOpBatchSpecs
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
        var op = new HasAnyOfAttributesOp(["a0", "a1"], requireAll: false);
        var ops = Substitute.For<IRelationalBatchOps>();
        var batch = Substitute.For<DbBatch>();
        var ctx = Ctx();

        op.AddToBatch(batch, ops, ctx, "doc", "1");

        ops.Received(1).AddHasAnyOfAttributesToBatch(
            batch, "doc", "1", Arg.Is<string[]>(a => a.SequenceEqual(new[] { "a0", "a1" })), ctx.SnapToken);
    }

    [Fact]
    public async Task ReadResultAsync_any_mode_is_true_when_at_least_one_row_comes_back()
    {
        var op = new HasAnyOfAttributesOp(["a0", "a1"], requireAll: false);
        var sink = new RecordingSink();

        await op.ReadResultAsync(new FakeStringResultReader(["a1"]), token: 7, sink, default);
        await op.ReadResultAsync(new FakeStringResultReader([]), token: 9, sink, default);

        sink.Completed.Should().ContainSingle(c => c.Token == 7 && c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 9 && !c.Result);
    }

    [Fact]
    public async Task ReadResultAsync_all_mode_requires_every_attribute_to_come_back()
    {
        var op = new HasAnyOfAttributesOp(["a0", "a1"], requireAll: true);
        var sink = new RecordingSink();

        await op.ReadResultAsync(new FakeStringResultReader(["a0", "a1"]), token: 1, sink, default);
        await op.ReadResultAsync(new FakeStringResultReader(["a0"]), token: 2, sink, default);

        sink.Completed.Should().ContainSingle(c => c.Token == 1 && c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 2 && !c.Result);
    }
}
