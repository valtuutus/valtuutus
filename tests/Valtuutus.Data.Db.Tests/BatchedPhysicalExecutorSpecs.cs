using System.Data.Common;
using FluentAssertions;
using NSubstitute;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Data.Db.Tests;

// Wiring specs for BatchedPhysicalExecutor — no real DB, everything through NSubstitute/fakes.
// All reader/batch calls below resolve to already-completed Tasks (Task.FromResult, ValueTask
// literals), so RunBatchAsync's await chain never actually yields — Submit's fire-and-forget
// (`_ = RunBatchAsync(...)`) runs to completion synchronously before Submit returns, letting these
// tests assert on the sink immediately without any wait/poll machinery (this is the same
// "synchronous completion inside Submit permitted" allowance IPhysicalExecutor's contract
// documents, not a shortcut specific to these tests).
public class BatchedPhysicalExecutorSpecs
{
    private static readonly Schema EmptySchema = new(new Dictionary<string, Entity>(), new Dictionary<string, Function>());

    // NSubstitute can't proxy ICheckOp (internal, and Valtuutus.Core isn't strong-named for the
    // DynamicProxyGenAssembly2 IVT trick) — a small hand-written fake stands in instead, deliberately
    // NOT implementing IBatchableCheckOp so it exercises the opaque/unbatchable fallback path.
    private sealed class FakeCheckOp(bool result) : ICheckOp
    {
        public int ExecuteCallCount { get; private set; }

        public ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx, string entityType,
            string entityId, CancellationToken ct)
        {
            ExecuteCallCount++;
            return new ValueTask<bool>(result);
        }

        public string Describe() => "FakeCheckOp";
    }

    private static CheckRequestContext Ctx() => new()
    {
        SubjectType = "user", SubjectId = "u1",
        SnapToken = new SnapToken("00000000000000000000000001"),
        Context = new Dictionary<string, object>()
    };

    private static PendingOp HasDirectRelationOp(int token, string entityType, string entityId, string relation) => new()
    {
        Token = token, Kind = OpKind.HasDirectRelation, EntityType = entityType, EntityId = entityId, Relation = relation
    };

    [Fact]
    public void Two_native_batchable_ops_share_one_ExecuteBatchAsync_round_trip()
    {
        var reader = Substitute.For<IDataReaderProvider, IRelationalBatchOps>();
        var batchOps = (IRelationalBatchOps)reader;
        var batch = Substitute.For<DbBatch>();
        batchOps.CreateBatch().Returns(batch);
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DbDataReader>(new FakeMultiResultReader(
                [new object[] { true }],
                [new object[] { false }])));

        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = Ctx();
        PendingOp[] ops =
        [
            HasDirectRelationOp(1, "household", "1", "owner"),
            HasDirectRelationOp(2, "household", "1", "admin"),
        ];

        executor.Submit(ops, ctx, sink, default);

        batchOps.Received(1).CreateBatch();
        batchOps.Received(1).ExecuteBatchAsync(batch, Arg.Any<CancellationToken>());
        batchOps.Received(1).AddHasDirectRelationToBatch(batch,
            new RelationTupleFilter { EntityType = "household", EntityId = "1", Relation = "owner", SnapToken = ctx.SnapToken },
            "u1");
        batchOps.Received(1).AddHasDirectRelationToBatch(batch,
            new RelationTupleFilter { EntityType = "household", EntityId = "1", Relation = "admin", SnapToken = ctx.SnapToken },
            "u1");
        sink.Completed.Should().ContainSingle(c => c.Token == 1 && c.Result);
        sink.Completed.Should().ContainSingle(c => c.Token == 2 && !c.Result);
        sink.Failed.Should().BeEmpty();
    }

    [Fact]
    public void Mixed_wave_splits_batchable_native_ops_from_opaque_non_batchable_CheckOp()
    {
        var reader = Substitute.For<IDataReaderProvider, IRelationalBatchOps>();
        var batchOps = (IRelationalBatchOps)reader;
        var batch = Substitute.For<DbBatch>();
        batchOps.CreateBatch().Returns(batch);
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DbDataReader>(new FakeMultiResultReader(
                [new object[] { true }],
                [new object[] { false }])));

        // Deliberately NOT IBatchableCheckOp — an opaque op the executor cannot fold into the batch.
        var opaqueOp = new FakeCheckOp(result: true);

        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = Ctx();
        // Two batchable ops (so the wave has something worth batching — Submit's single-op fast
        // path would otherwise skip the batch machinery entirely) plus one opaque op.
        PendingOp[] ops =
        [
            HasDirectRelationOp(1, "household", "1", "owner"),
            HasDirectRelationOp(2, "household", "1", "admin"),
            new PendingOp { Token = 3, Kind = OpKind.CheckOp, EntityType = "doc", EntityId = "9", Op = opaqueOp },
        ];

        executor.Submit(ops, ctx, sink, default);

        // Both batchable ops went through the batch, never the opaque one.
        batchOps.Received(1).AddHasDirectRelationToBatch(batch,
            Arg.Is<RelationTupleFilter>(f => f.Relation == "owner"), "u1");
        batchOps.Received(1).AddHasDirectRelationToBatch(batch,
            Arg.Is<RelationTupleFilter>(f => f.Relation == "admin"), "u1");
        // The opaque op ran through the individual fallback path instead (PhysicalOpRunner), which
        // calls ICheckOp.Execute directly — proof it never touched the batch.
        opaqueOp.ExecuteCallCount.Should().Be(1);
        sink.Completed.Should().Contain(c => c.Token == 1 && c.Result);
        sink.Completed.Should().Contain(c => c.Token == 2 && !c.Result);
        sink.Completed.Should().Contain(c => c.Token == 3 && c.Result);
    }

    [Fact]
    public void Non_relational_reader_falls_back_to_full_individual_execution_for_every_op()
    {
        // No IRelationalBatchOps on this substitute — matches DefaultPhysicalExecutor's behavior
        // for every op in the wave, not just the ones a batch couldn't hold.
        var reader = Substitute.For<IDataReaderProvider>();
        reader.HasDirectRelation(Arg.Any<RelationTupleFilter>(), "u1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var executor = new BatchedPhysicalExecutor(EmptySchema, null) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = Ctx();
        PendingOp[] ops = [HasDirectRelationOp(1, "household", "1", "owner")];

        executor.Submit(ops, ctx, sink, default);

        sink.Completed.Should().ContainSingle(c => c.Token == 1 && c.Result);
    }

    [Fact]
    public void Single_op_wave_skips_the_batch_entirely_even_when_the_reader_supports_batching()
    {
        // Reader supports batching, but there's only one op in the wave — nothing to gain from a
        // DbBatch of one command, so Submit's fast path should dispatch it directly instead
        // (a DbBatch of one command costs more than dispatching it directly).
        var reader = Substitute.For<IDataReaderProvider, IRelationalBatchOps>();
        var batchOps = (IRelationalBatchOps)reader;
        reader.HasDirectRelation(Arg.Any<RelationTupleFilter>(), "u1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = Ctx();
        PendingOp[] ops = [HasDirectRelationOp(1, "household", "1", "owner")];

        executor.Submit(ops, ctx, sink, default);

        batchOps.DidNotReceive().CreateBatch();
        batchOps.DidNotReceive().ExecuteBatchAsync(Arg.Any<DbBatch>(), Arg.Any<CancellationToken>());
        sink.Completed.Should().ContainSingle(c => c.Token == 1 && c.Result);
    }

    [Fact]
    public void A_batch_fault_fails_every_not_yet_reported_op_in_the_batch()
    {
        var reader = Substitute.For<IDataReaderProvider, IRelationalBatchOps>();
        var batchOps = (IRelationalBatchOps)reader;
        var batch = Substitute.For<DbBatch>();
        batchOps.CreateBatch().Returns(batch);
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DbDataReader>(new InvalidOperationException("boom")));

        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = Ctx();
        PendingOp[] ops =
        [
            HasDirectRelationOp(1, "household", "1", "owner"),
            HasDirectRelationOp(2, "household", "1", "admin"),
        ];

        executor.Submit(ops, ctx, sink, default);

        sink.Completed.Should().BeEmpty();
        sink.Failed.Select(f => f.Token).Should().BeEquivalentTo([1, 2]);
    }
}
