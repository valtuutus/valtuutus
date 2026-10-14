using System.Data.Common;
using AwesomeAssertions;
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
        #pragma warning disable CA2000 // The executor owns and disposes the batch reader.
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DbDataReader>(new FakeMultiResultReader(
                [new object[] { true }],
                [new object[] { false }])));
        #pragma warning restore CA2000

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
        #pragma warning disable CA2000 // The executor owns and disposes the batch reader.
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DbDataReader>(new FakeMultiResultReader(
                [new object[] { true }],
                [new object[] { false }])));
        #pragma warning restore CA2000

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

    // ── snap token resolved in the first round trip ─────────────────────────────────────────────────

    private const string ResolvedToken = "01JZZZZZZZZZZZZZZZZZZZZZZZ";

    // Records the token visible through the context at the moment the op actually runs.
    private sealed class TokenCapturingCheckOp : ICheckOp
    {
        public SnapToken? SeenToken { get; private set; }

        public ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx, string entityType,
            string entityId, CancellationToken ct)
        {
            SeenToken = ctx.SnapToken;
            return new ValueTask<bool>(true);
        }

        public string Describe() => "TokenCapturingCheckOp";
    }

    private static CheckRequestContext PendingCtx() => new()
    {
        SubjectType = "user", SubjectId = "u1", SnapToken = default,
        Context = new Dictionary<string, object>(), ResolveLatestSnapToken = true
    };

    private static PendingOp CapturingOp(int token, TokenCapturingCheckOp op) =>
        new() { Token = token, Kind = OpKind.CheckOp, EntityType = "doc", EntityId = "9", Op = op };

    private static (IDataReaderProvider Reader, IRelationalBatchOps BatchOps, IRelationalBatchOps BatchResolving,
        IRelationalBatchOps BatchResolvedView, DbBatch Batch) BatchResolvingProvider()
    {
        var reader = Substitute.For<IDataReaderProvider, IRelationalBatchOps>();
        var batchOps = (IRelationalBatchOps)reader;
        var batchResolvedView = Substitute.For<IRelationalBatchOps>();
        var batch = Substitute.For<DbBatch>();
        batchOps.CreateBatch().Returns(batch);
        batchOps.UsingBatchResolvedSnapToken.Returns(batchResolvedView);
        return (reader, batchOps, batchOps, batchResolvedView, batch);
    }

    [Fact]
    public void Executor_can_resolve_in_band_only_when_its_batch_ops_can()
    {
        var plain = Substitute.For<IRelationalBatchOps>();
        plain.UsingBatchResolvedSnapToken.Returns((IRelationalBatchOps?)null);
        new BatchedPhysicalExecutor(EmptySchema, plain).CanResolveSnapTokenInFirstRoundTrip.Should().BeFalse();
        new BatchedPhysicalExecutor(EmptySchema, null).CanResolveSnapTokenInFirstRoundTrip.Should().BeFalse();
        new BatchedPhysicalExecutor(EmptySchema, BatchResolvingProvider().BatchOps).CanResolveSnapTokenInFirstRoundTrip.Should().BeTrue();
    }

    [Fact]
    public void Pending_token_single_op_wave_resolves_the_token_in_the_same_batch()
    {
        var (reader, batchOps, batchResolving, batchResolvedView, batch) = BatchResolvingProvider();
        #pragma warning disable CA2000 // The executor owns and disposes the batch reader.
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DbDataReader>(new FakeMultiResultReader(
                [new object[] { ResolvedToken }],
                [new object[] { true }])));
        #pragma warning restore CA2000

        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = PendingCtx();

        executor.Submit([HasDirectRelationOp(1, "household", "1", "owner")], ctx, sink, default);

        Received.InOrder(() =>
        {
            batchResolving.AddResolveLatestSnapTokenToBatch(batch);
            batchResolvedView.AddHasDirectRelationToBatch(batch, Arg.Is<RelationTupleFilter>(f => f.Relation == "owner"), "u1");
        });
        // Commands never go through the parameter-bound catalog while the token is resolved inside the batch, and
        // no separate token round trip is made.
        batchOps.DidNotReceiveWithAnyArgs().AddHasDirectRelationToBatch(default!, default, default!);
        reader.DidNotReceive().GetLatestSnapToken(Arg.Any<CancellationToken>());
        batchOps.Received(1).ExecuteBatchAsync(batch, Arg.Any<CancellationToken>());
        sink.Completed.Should().ContainSingle(c => c.Token == 1 && c.Result);

        // A later wave of the same request runs against the token the first batch resolved.
        var later = new TokenCapturingCheckOp();
        executor.Submit([CapturingOp(2, later)], ctx, sink, default);
        later.SeenToken.Should().Be(new SnapToken(ResolvedToken));
    }

    [Fact]
    public void Empty_token_result_resolves_to_the_minimum_token()
    {
        var (reader, batchOps, _, _, batch) = BatchResolvingProvider();
        #pragma warning disable CA2000 // The executor owns and disposes the batch reader.
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DbDataReader>(new FakeMultiResultReader(
                [],
                [new object[] { false }])));
        #pragma warning restore CA2000

        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var ctx = PendingCtx();

        executor.Submit([HasDirectRelationOp(1, "household", "1", "owner")], ctx, new RecordingSink(), default);

        var later = new TokenCapturingCheckOp();
        executor.Submit([CapturingOp(2, later)], ctx, new RecordingSink(), default);
        later.SeenToken.Should().Be(SnapToken.MinValue);
    }

    [Fact]
    public async Task Unbatchable_op_in_the_resolving_wave_runs_only_after_the_token_is_published()
    {
        var (reader, batchOps, _, _, batch) = BatchResolvingProvider();
        var batchResult = new TaskCompletionSource<DbDataReader>();
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>()).Returns(batchResult.Task);

        var opaque = new TokenCapturingCheckOp();
        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = PendingCtx();

        executor.Submit(
        [
            HasDirectRelationOp(1, "household", "1", "owner"),
            CapturingOp(2, opaque),
        ], ctx, sink, default);

        opaque.SeenToken.Should().BeNull("the opaque op needs the token, which isn't resolved yet");

        #pragma warning disable CA2000 // The executor owns and disposes the batch reader.
        batchResult.SetResult(new FakeMultiResultReader([new object[] { ResolvedToken }], [new object[] { true }]));
        #pragma warning restore CA2000
        for (var i = 0; i < 100 && sink.Completed.Count < 2; i++) await Task.Delay(10);

        opaque.SeenToken.Should().Be(new SnapToken(ResolvedToken));
        sink.Completed.Should().Contain(c => c.Token == 1 && c.Result);
        sink.Completed.Should().Contain(c => c.Token == 2 && c.Result);
    }

    [Fact]
    public async Task Wave_submitted_while_the_token_is_still_resolving_waits_and_uses_that_token()
    {
        var (reader, batchOps, _, _, batch) = BatchResolvingProvider();
        var batchResult = new TaskCompletionSource<DbDataReader>();
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>()).Returns(batchResult.Task);

        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = PendingCtx();
        var second = new TokenCapturingCheckOp();

        executor.Submit([HasDirectRelationOp(1, "household", "1", "owner")], ctx, sink, default);
        executor.Submit([CapturingOp(2, second)], ctx, sink, default);

        second.SeenToken.Should().BeNull();
        batchOps.Received(1).CreateBatch();

        #pragma warning disable CA2000 // The executor owns and disposes the batch reader.
        batchResult.SetResult(new FakeMultiResultReader([new object[] { ResolvedToken }], [new object[] { true }]));
        #pragma warning restore CA2000
        // The waiting wave resumes on a continuation (the token source never runs them inline).
        for (var i = 0; i < 100 && second.SeenToken is null; i++) await Task.Delay(10);

        second.SeenToken.Should().Be(new SnapToken(ResolvedToken));
        sink.Completed.Should().Contain(c => c.Token == 2 && c.Result);
    }

    [Fact]
    public async Task Resolving_batch_fault_fails_the_token_and_every_op_waiting_on_it()
    {
        var (reader, batchOps, _, _, batch) = BatchResolvingProvider();
        var batchResult = new TaskCompletionSource<DbDataReader>();
        batchOps.ExecuteBatchAsync(batch, Arg.Any<CancellationToken>()).Returns(batchResult.Task);

        var executor = new BatchedPhysicalExecutor(EmptySchema, batchOps) { Reader = reader };
        var sink = new RecordingSink();
        var ctx = PendingCtx();

        executor.Submit(
        [
            HasDirectRelationOp(1, "household", "1", "owner"),
            CapturingOp(2, new TokenCapturingCheckOp()),
        ], ctx, sink, default);
        executor.Submit([HasDirectRelationOp(3, "household", "1", "admin")], ctx, sink, default);

        batchResult.SetException(new InvalidOperationException("boom"));
        for (var i = 0; i < 100 && sink.Failed.Count < 3; i++) await Task.Delay(10);

        sink.Completed.Should().BeEmpty();
        sink.Failed.Select(f => f.Token).Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public void Context_that_asks_the_executor_to_resolve_the_token_has_no_token_to_hand_out()
    {
        FluentActions.Invoking(() => PendingCtx().SnapToken).Should().Throw<InvalidOperationException>();
        Ctx().SnapToken.Should().Be(new SnapToken("00000000000000000000000001"));
    }
}
