using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;
using Valtuutus.Core.Schemas;
using Valtuutus.Data.InMemory;

namespace Valtuutus.Data.InMemory.Tests.V2;

public class CheckPlanExecutorSpecs
{
    internal static async Task<(IServiceProvider sp, Schema schema, IDataReaderProvider reader)> Arrange(
        string schemaText, RelationTuple[] tuples, AttributeTuple[]? attributes = null)
    {
        var services = new ServiceCollection().AddValtuutusCore(schemaText);
        services.AddInMemory();
        var sp = services.BuildServiceProvider().CreateScope().ServiceProvider;
        await sp.GetRequiredService<IDataWriterProvider>().Write(tuples, attributes ?? [], default);
        return (sp, sp.GetRequiredService<Schema>(), sp.GetRequiredService<IDataReaderProvider>());
    }

    internal sealed class RecordingSink : IOpCompletionSink
    {
        public readonly List<(int Token, bool Result)> Completed = [];
        public readonly List<(int Token, Exception Error)> Failed = [];
        public readonly List<(int Token, object Payload)> Payloads = [];
        public void Complete(int token, bool result) { lock (Completed) Completed.Add((token, result)); }
        public void CompleteWithPayload(int token, object payload) { lock (Payloads) Payloads.Add((token, payload)); }
        public void Fail(int token, Exception error) { lock (Failed) Failed.Add((token, error)); }
    }

    // Records every submitted op, then forwards to the real executor — lets a test assert on
    // query SHAPE (one batched op vs N singles) while the check still runs for real.
    internal sealed class RecordingPhysicalExecutor(IPhysicalExecutor inner) : IPhysicalExecutor
    {
        public readonly List<PendingOp> Submitted = [];
        public void Submit(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
        {
            lock (Submitted)
                foreach (var op in ops) Submitted.Add(op);
            inner.Submit(ops, ctx, sink, ct);
        }
    }

    // Test double letting a test deterministically simulate a genuinely still-in-flight op
    // (unlike InMemory's real provider, which resolves synchronously and can never produce one).
    // Ops whose relation name is in HeldRelations don't complete until the test explicitly
    // calls Release — everything else completes immediately with the configured result.
    internal sealed class ControllablePhysicalExecutor : IPhysicalExecutor
    {
        public readonly HashSet<string> HeldRelations = [];
        public bool ImmediateResult = true;
        private readonly Dictionary<string, List<(int Token, IOpCompletionSink Sink)>> _held = new();

        public void Submit(ReadOnlySpan<PendingOp> ops, CheckRequestContext ctx, IOpCompletionSink sink, CancellationToken ct)
        {
            foreach (var op in ops)
            {
                if (op.Relation is not null && HeldRelations.Contains(op.Relation))
                {
                    lock (_held)
                    {
                        if (!_held.TryGetValue(op.Relation, out var list)) _held[op.Relation] = list = [];
                        list.Add((op.Token, sink));
                    }
                }
                else
                {
                    sink.Complete(op.Token, ImmediateResult);
                }
            }
        }

        // Releases every op held under `relation` with `result`, targeting whichever sink was
        // current when Submit ran for it — this is exactly how a genuine straggler behaves: it
        // completes against whatever IOpCompletionSink it captured at dispatch time, regardless
        // of what that instance is doing by the time it actually finishes.
        public void Release(string relation, bool result)
        {
            List<(int Token, IOpCompletionSink Sink)> list;
            lock (_held) { list = _held.TryGetValue(relation, out var l) ? l : []; }
            foreach (var (token, sink) in list)
                sink.Complete(token, result);
        }
    }

    [Fact]
    public async Task DefaultPhysicalExecutor_runs_HasDirectRelation_op()
    {
        var (_, schema, reader) = await Arrange("""
            entity user {}
            entity doc { relation owner @user; }
            """, [new RelationTuple("doc", "1", "owner", "user", "u1")]);

        var ctx = new CheckRequestContext
            { SubjectType = "user", SubjectId = "u1", SnapToken = (await reader.GetLatestSnapToken(default))!.Value, Context = new Dictionary<string, object>() };
        var sink = new RecordingSink();
        var physical = new DefaultPhysicalExecutor(schema) { Reader = reader };

        physical.Submit([new PendingOp { Token = 1, Kind = OpKind.HasDirectRelation, EntityType = "doc", EntityId = "1", Relation = "owner" }],
            ctx, sink, CancellationToken.None);

        await WaitFor(() => sink.Completed.Count == 1);
        sink.Completed.Should().ContainSingle().Which.Should().Be((1, true));
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue();
    }

    internal static async Task<bool> RunCheck(string schemaText, RelationTuple[] tuples, AttributeTuple[]? attributes,
        string entityType, string entityId, string permission, string subjectType = "user", string subjectId = "u1",
        int depth = 10, string? subjectRelation = null)
    {
        var (sp, schema, reader) = await Arrange(schemaText, tuples, attributes);
        var snap = await Valtuutus.Core.Engines.SnapTokenUtils.ResolveLatest(reader, null, default);
        var ctx = new CheckRequestContext
            { SubjectType = subjectType, SubjectId = subjectId, SnapToken = snap, Context = new Dictionary<string, object>() };
        var executor = new CheckPlanExecutor(schema, new CheckPlanCache(schema))
            { Physical = new DefaultPhysicalExecutor(schema) { Reader = reader } };
        var results = await executor.ExecuteAsync(
            [new CheckRootRequest(entityType, entityId, permission, subjectRelation, depth)], ctx, default);
        return results[0];
    }

    private const string DocSchema = """
        entity user {}
        entity doc {
            relation owner @user;
            attribute published bool;
            permission view := owner;
        }
        """;

    [Fact]
    public async Task Direct_relation_root_true_and_false()
    {
        (await RunCheck(DocSchema, [new RelationTuple("doc", "1", "owner", "user", "u1")], null,
            "doc", "1", "owner")).Should().BeTrue();
        (await RunCheck(DocSchema, [new RelationTuple("doc", "1", "owner", "user", "u1")], null,
            "doc", "1", "owner", subjectId: "u2")).Should().BeFalse();
    }

    [Fact]
    public async Task Attribute_root_reads_bool_attribute()
    {
        (await RunCheck(DocSchema, [], [new AttributeTuple("doc", "1", "published", System.Text.Json.Nodes.JsonValue.Create(true))],
            "doc", "1", "published")).Should().BeTrue();
        (await RunCheck(DocSchema, [], [], "doc", "1", "published")).Should().BeFalse();
    }

    [Fact]
    public async Task PlanRef_resolves_through_permission_indirection()
    {
        (await RunCheck(DocSchema, [new RelationTuple("doc", "1", "owner", "user", "u1")], null,
            "doc", "1", "view")).Should().BeTrue();
    }

    [Fact]
    public async Task Depth_zero_returns_false()
    {
        (await RunCheck(DocSchema, [new RelationTuple("doc", "1", "owner", "user", "u1")], null,
            "doc", "1", "view", depth: 0)).Should().BeFalse();
    }

    [Fact]
    public async Task SubjectRelation_self_reference_short_circuits_true()
    {
        // Mirrors CheckEngine.cs:220-224.
        (await RunCheck(DocSchema, [], null, "doc", "1", "owner",
            subjectType: "doc", subjectId: "1", subjectRelation: "owner")).Should().BeTrue();
    }

    private const string ExprSchema = """
        entity user {}
        entity doc {
            relation owner @user;
            relation editor @user;
            relation banned @user;
            permission view := owner or editor;
            permission edit := owner and editor;
            permission read := editor and not(banned);
        }
        """;

    [Theory]
    [InlineData("view", new[] { "owner" }, true)]
    [InlineData("view", new string[0], false)]
    [InlineData("edit", new[] { "owner" }, false)]
    [InlineData("edit", new[] { "owner", "editor" }, true)]
    [InlineData("read", new[] { "editor" }, true)]
    [InlineData("read", new[] { "editor", "banned" }, false)]
    public async Task Expressions_combine_and_short_circuit(string permission, string[] relations, bool expected)
    {
        var tuples = relations.Select(r => new RelationTuple("doc", "1", r, "user", "u1")).ToArray();
        (await RunCheck(ExprSchema, tuples, null, "doc", "1", permission)).Should().Be(expected);
    }

    [Fact]
    public async Task Wide_union_forces_frame_array_growth_without_corruption()
    {
        // Regression test: StartExpression's spawn loop must not hold a `ref Frame` across
        // SpawnFrame calls (SpawnFrame can grow/replace the pooled _frames array mid-loop).
        // 24 relations exceeds the executor's initial frame-pool capacity of 16, so this
        // schema is chosen specifically to force at least one growth inside the union's fan-out.
        const int branchCount = 24;
        var relationDecls = string.Join("\n", Enumerable.Range(0, branchCount).Select(i => $"    relation r{i} @user;"));
        var unionExpr = string.Join(" or ", Enumerable.Range(0, branchCount).Select(i => $"r{i}"));
        var schemaText = $$"""
            entity user {}
            entity doc {
            {{relationDecls}}
                permission view := {{unionExpr}};
            }
            """;

        // Only the last branch (r23) is granted — forces every earlier sibling frame to spawn,
        // resolve false, and get walked past before the true one completes the union.
        var tuples = new[] { new RelationTuple("doc", "1", $"r{branchCount - 1}", "user", "u1") };
        (await RunCheck(schemaText, tuples, null, "doc", "1", "view")).Should().BeTrue();
    }

    private const string TtuSchema = """
        entity user {}
        entity organization {
            relation admin @user;
        }
        entity group {
            relation member @user;
        }
        entity folder {
            relation parent @organization;
            relation viewers @group#member;
            relation shared @group;
            permission read := parent.admin;
            permission see := shared.member;
        }
        """;

    [Fact]
    public async Task Ttu_fast_path_resolves_single_hop_join()
    {
        RelationTuple[] tuples =
        [
            new("folder", "f1", "parent", "organization", "o1"),
            new("organization", "o1", "admin", "user", "u1"),
        ];
        (await RunCheck(TtuSchema, tuples, null, "folder", "f1", "read")).Should().BeTrue();
        (await RunCheck(TtuSchema, tuples, null, "folder", "f1", "read", subjectId: "u2")).Should().BeFalse();
    }

    [Fact]
    public async Task Ttu_with_no_tuples_is_false()
    {
        (await RunCheck(TtuSchema, [], null, "folder", "f1", "read")).Should().BeFalse();
    }

    [Fact]
    public async Task Ttu_fanout_over_multiple_tuples_unions()
    {
        RelationTuple[] tuples =
        [
            new("folder", "f1", "shared", "group", "g1"),
            new("folder", "f1", "shared", "group", "g2"),
            new("group", "g2", "member", "user", "u1"),
        ];
        (await RunCheck(TtuSchema, tuples, null, "folder", "f1", "see")).Should().BeTrue();
    }

    [Fact]
    public async Task Fn_leaf_evaluates_attribute_expression()
    {
        const string s = """
            entity user {}
            entity doc {
                relation owner @user;
                attribute status string;
                permission view := owner or isPublic(status);
            }
            fn isPublic(status string) => status == "public";
            """;
        (await RunCheck(s, [], [new AttributeTuple("doc", "1", "status", System.Text.Json.Nodes.JsonValue.Create("public"))],
            "doc", "1", "view")).Should().BeTrue();
        (await RunCheck(s, [], [new AttributeTuple("doc", "1", "status", System.Text.Json.Nodes.JsonValue.Create("private"))],
            "doc", "1", "view")).Should().BeFalse();
    }

    [Fact]
    public async Task Relation_with_subject_relation_paths_recurses()
    {
        const string s = """
            entity user {}
            entity group {
                relation member @user;
            }
            entity doc {
                relation viewer @user @group#member;
            }
            """;
        RelationTuple[] tuples =
        [
            new("doc", "1", "viewer", "group", "g1", "member"),
            new("group", "g1", "member", "user", "u1"),
        ];
        (await RunCheck(s, tuples, null, "doc", "1", "viewer")).Should().BeTrue();
        (await RunCheck(s, tuples, null, "doc", "1", "viewer", subjectId: "u2")).Should().BeFalse();
    }

    [Fact]
    public async Task MemoNode_shares_result_within_one_plan()
    {
        const string s = """
            entity user {}
            entity folder {
                relation owner @user;
                relation editor @user;
                permission admin := owner or (editor and owner);
            }
            """;
        (await RunCheck(s, [new RelationTuple("folder", "f1", "owner", "user", "u1")], null,
            "folder", "f1", "admin")).Should().BeTrue();
        (await RunCheck(s, [new RelationTuple("folder", "f1", "editor", "user", "u1")], null,
            "folder", "f1", "admin")).Should().BeFalse();
    }

    [Fact]
    public async Task AddValtuutusCheckV2_replaces_the_engine_and_answers_checks()
    {
        var services = new ServiceCollection().AddValtuutusCore(DocSchema);
        services.AddInMemory();
        services.AddValtuutusCheckV2();
        var sp = services.BuildServiceProvider().CreateScope().ServiceProvider;
        await sp.GetRequiredService<IDataWriterProvider>()
            .Write([new RelationTuple("doc", "1", "owner", "user", "u1")], [], default);

        var engine = sp.GetRequiredService<ICheckEngine>();
        engine.Should().BeOfType<CheckEngineV2>();

        (await engine.Check(new CheckRequest("doc", "1", "view", "user", "u1"), default)).Should().BeTrue();
        var perms = await engine.SubjectPermission(
            new SubjectPermissionRequest { EntityType = "doc", EntityId = "1", SubjectType = "user", SubjectId = "u1" }, default);
        perms["view"].Should().BeTrue();
        (await engine.Explain(new CheckRequest("doc", "1", "view", "user", "u1"), default)).Result.Should().BeTrue();
    }

    [Fact]
    public async Task Pooled_executor_carries_no_state_across_interleaved_calls()
    {
        // Regression test for CheckPlanExecutorPool (Task 16 Stage 4): the same pooled
        // CheckPlanExecutor instance is very likely to be reused across these calls (DI-singleton
        // pool, sequential awaits, one outstanding rent at a time). Every call here targets a
        // DIFFERENT entity/subject/permission/expected-result — if any mutable state (frames,
        // ready-list, memo dictionary, mailbox) leaked from a previous ExecuteAsync into this
        // one, at least one of these would return the wrong answer or throw.
        const string s = """
            entity user {}
            entity doc {
                relation owner @user;
                relation editor @user;
                permission admin := owner or (editor and owner);
                permission view := owner or editor;
            }
            """;
        var services = new ServiceCollection().AddValtuutusCore(s);
        services.AddInMemory();
        services.AddValtuutusCheckV2();
        var scoped = services.BuildServiceProvider().CreateScope().ServiceProvider;
        await scoped.GetRequiredService<IDataWriterProvider>().Write([
            new RelationTuple("doc", "1", "owner", "user", "alice"),
            new RelationTuple("doc", "2", "editor", "user", "bob"),
            new RelationTuple("doc", "3", "owner", "user", "carol"),
            new RelationTuple("doc", "3", "editor", "user", "carol"),
        ], [], default);
        var engine = scoped.GetRequiredService<ICheckEngine>();

        (bool Expected, CheckRequest Req)[] cases =
        [
            (true, new CheckRequest("doc", "1", "view", "user", "alice")),   // owner
            (false, new CheckRequest("doc", "1", "admin", "user", "bob")),   // no relation at all
            (true, new CheckRequest("doc", "2", "view", "user", "bob")),     // editor only
            (false, new CheckRequest("doc", "2", "admin", "user", "bob")),   // editor alone fails admin
            (true, new CheckRequest("doc", "3", "admin", "user", "carol")),  // owner+editor
            (false, new CheckRequest("doc", "3", "view", "user", "alice")),  // wrong subject
        ];

        // Interleave twice to increase the odds of hitting the same pooled instance back-to-back
        // with a different plan/entity/subject each time.
        for (var round = 0; round < 2; round++)
            foreach (var (expected, req) in cases)
                (await engine.Check(req, default)).Should().Be(expected,
                    $"round {round} permission={req.Permission} entity={req.EntityId} subject={req.SubjectId}");
    }

    [Fact]
    public async Task Short_circuit_with_many_queued_stragglers_does_not_corrupt_a_later_pooled_check()
    {
        // Broad sanity check with the real InMemory provider — NOT a proof of the straggler race
        // itself. InMemory resolves every op fully synchronously, so by the time a wide Union's
        // DrainReady loop finishes submitting all branches, every branch's completion is already
        // sitting in the mailbox queue (not a genuinely still-running RunAsync) — reassigning
        // _mailbox on the next ExecuteAsync call harmlessly discards those. The real race
        // (a completion landing on a REUSED executor's live state) needs a provider that can
        // still be mid-flight at reuse time; see
        // DrainStragglersAsync_blocks_until_a_genuinely_still_running_op_completes below for a
        // deterministic reproduction of that exact mechanism.
        const int branchCount = 20;
        var relationDecls = string.Join("\n", Enumerable.Range(0, branchCount).Select(i => $"    relation r{i} @user;"));
        var unionExpr = string.Join(" or ", Enumerable.Range(0, branchCount).Select(i => $"r{i}"));
        var schemaText = $$"""
            entity user {}
            entity doc {
            {{relationDecls}}
                relation unrelated @user;
                permission view := {{unionExpr}};
            }
            """;

        var services = new ServiceCollection().AddValtuutusCore(schemaText);
        services.AddInMemory();
        services.AddValtuutusCheckV2();
        var scoped = services.BuildServiceProvider().CreateScope().ServiceProvider;
        await scoped.GetRequiredService<IDataWriterProvider>().Write(
            [new RelationTuple("doc", "1", "r0", "user", "u1")], [], default);
        var engine = scoped.GetRequiredService<ICheckEngine>();

        for (var i = 0; i < 50; i++)
        {
            (await engine.Check(new CheckRequest("doc", "1", "view", "user", "u1"), default)).Should().BeTrue();
            (await engine.Check(new CheckRequest("doc", "2", "unrelated", "user", "u1"), default)).Should().BeFalse(
                "doc/2 has no tuples at all — any straggler leakage from the prior wide-union short-circuit would corrupt this");
        }
    }

    [Fact]
    public async Task DrainStragglersAsync_blocks_until_a_genuinely_still_running_op_completes()
    {
        // Deterministic reproduction of the mechanism DrainStragglersAsync exists to guard:
        // a Union with two branches where one (r0) resolves immediately true — short-circuiting
        // and letting ExecuteAsync return — while the other (r1) is still genuinely in flight
        // (ControllablePhysicalExecutor holds it open on purpose; the real InMemory provider
        // can never produce this, since it always resolves synchronously). If the pool returned
        // this executor to service the moment ExecuteAsync answered, a caller could rent it for
        // an unrelated request while r1's eventual completion is still pending — and when r1
        // finally lands, it would call sink.Complete against whatever _frames/_mailbox that
        // unrelated request has by then, using r1's now-stale token.
        // r1 is an attribute, not a relation, so GroupSiblingDirectRelations (which only
        // recognizes direct-relation refs) leaves it out and the union stays two independent
        // ops — otherwise sibling-batching would fuse r0/r1 into one MultiDirectNode op,
        // defeating this test's premise of one instant leg and one genuinely outstanding leg.
        const string s = """
            entity user {}
            entity doc {
                relation r0 @user;
                attribute r1 bool;
                permission view := r0 or r1;
            }
            """;
        var (_, schema, _) = await Arrange(s, []);
        var executor = new CheckPlanExecutor(schema, new CheckPlanCache(schema));
        var physical = new ControllablePhysicalExecutor { ImmediateResult = true };
        physical.HeldRelations.Add("r1");
        executor.Physical = physical;

        var ctx = new CheckRequestContext
            { SubjectType = "user", SubjectId = "u1", SnapToken = default, Context = new Dictionary<string, object>() };

        var results = await executor.ExecuteAsync(
            [new CheckRootRequest("doc", "1", "view", null, 10)], ctx, default, memoizeRoots: false);
        results[0].Should().BeTrue("r0 resolves immediately true and short-circuits the union");

        // r1's op is still genuinely outstanding at this point — the executor must not be
        // reported idle (safe to pool) until it lands.
        var drainTask = executor.DrainStragglersAsync();
        await Task.Delay(50);
        drainTask.IsCompleted.Should().BeFalse(
            "r1's op is still outstanding — DrainStragglersAsync must wait for it, not return early");

        physical.Release("r1", false);
        await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
        drainTask.IsCompletedSuccessfully.Should().BeTrue("releasing the last outstanding op must let the drain finish promptly");
    }

    [Fact]
    public async Task ExecuteSingleAsync_reuses_its_buffer_without_leaking_state_across_calls()
    {
        // ExecuteSingleAsync writes each call's root into a buffer owned by the pooled instance
        // (see CheckPlanExecutor._singleRootBuffer) instead of allocating a fresh array — this
        // proves that reuse doesn't leak the previous call's root into the next one.
        const string s = """
            entity user {}
            entity doc {
                relation owner @user;
                relation editor @user;
            }
            """;
        var (_, schema, reader) = await Arrange(s, [
            new RelationTuple("doc", "1", "owner", "user", "alice"),
            new RelationTuple("doc", "2", "editor", "user", "bob"),
        ]);
        var executor = new CheckPlanExecutor(schema, new CheckPlanCache(schema))
            { Physical = new DefaultPhysicalExecutor(schema) { Reader = reader } };
        var snap = (await reader.GetLatestSnapToken(default))!.Value;

        var ctxAlice = new CheckRequestContext
            { SubjectType = "user", SubjectId = "alice", SnapToken = snap, Context = new Dictionary<string, object>() };
        var ctxBob = new CheckRequestContext
            { SubjectType = "user", SubjectId = "bob", SnapToken = snap, Context = new Dictionary<string, object>() };

        for (var i = 0; i < 10; i++)
        {
            (await executor.ExecuteSingleAsync(new CheckRootRequest("doc", "1", "owner", null, 10), ctxAlice, default))[0]
                .Should().BeTrue($"iteration {i}: alice owns doc/1");
            (await executor.ExecuteSingleAsync(new CheckRootRequest("doc", "2", "owner", null, 10), ctxBob, default))[0]
                .Should().BeFalse($"iteration {i}: bob is editor, not owner, of doc/2");
            (await executor.ExecuteSingleAsync(new CheckRootRequest("doc", "2", "editor", null, 10), ctxBob, default))[0]
                .Should().BeTrue($"iteration {i}: bob edits doc/2");
        }
    }

    private const string HouseholdSchema = """
        entity user {}
        entity household {
            relation owner @user;
            relation admin @user;
            relation member @user;
            permission view := owner or admin or member;
            permission manage := owner and admin;
        }
        """;

    [Fact]
    public async Task MultiDirect_union_true_when_any_relation_matches()
    {
        (await RunCheck(HouseholdSchema, [new RelationTuple("household", "1", "member", "user", "u1")], null,
            "household", "1", "view")).Should().BeTrue();
    }

    [Fact]
    public async Task MultiDirect_union_false_when_no_relation_matches()
    {
        (await RunCheck(HouseholdSchema, [new RelationTuple("household", "1", "member", "user", "someone-else")], null,
            "household", "1", "view")).Should().BeFalse();
    }

    [Fact]
    public async Task MultiDirect_intersect_requires_every_relation()
    {
        RelationTuple[] onlyOwner = [new RelationTuple("household", "1", "owner", "user", "u1")];
        (await RunCheck(HouseholdSchema, onlyOwner, null, "household", "1", "manage")).Should().BeFalse();

        RelationTuple[] both =
        [
            new RelationTuple("household", "1", "owner", "user", "u1"),
            new RelationTuple("household", "1", "admin", "user", "u1"),
        ];
        (await RunCheck(HouseholdSchema, both, null, "household", "1", "manage")).Should().BeTrue();
    }

    [Fact]
    public async Task MultiDirect_intersect_is_not_fooled_by_duplicate_tuples()
    {
        // Two identical owner tuples, no admin: a count over ROWS would see 2 == 2 relations and
        // wrongly pass; the HashSet return type guarantees set semantics (see the
        // HasAnyOfDirectRelations doc remarks).
        RelationTuple[] dupOwner =
        [
            new RelationTuple("household", "1", "owner", "user", "u1"),
            new RelationTuple("household", "1", "owner", "user", "u1"),
        ];
        (await RunCheck(HouseholdSchema, dupOwner, null, "household", "1", "manage")).Should().BeFalse();
    }

    [Fact]
    public async Task MultiDirect_submits_one_batched_op_instead_of_three_singles()
    {
        var (_, schema, reader) = await Arrange(HouseholdSchema,
            [new RelationTuple("household", "1", "member", "user", "u1")]);
        var snap = await Valtuutus.Core.Engines.SnapTokenUtils.ResolveLatest(reader, null, default);
        var ctx = new CheckRequestContext
            { SubjectType = "user", SubjectId = "u1", SnapToken = snap, Context = new Dictionary<string, object>() };
        var recording = new RecordingPhysicalExecutor(new DefaultPhysicalExecutor(schema) { Reader = reader });
        var executor = new CheckPlanExecutor(schema, new CheckPlanCache(schema)) { Physical = recording };

        var results = await executor.ExecuteAsync(
            [new CheckRootRequest("household", "1", "view", null, 10)], ctx, default);

        results[0].Should().BeTrue();
        recording.Submitted.Should().ContainSingle();
        recording.Submitted[0].Kind.Should().Be(OpKind.HasAnyOfDirectRelations);
        recording.Submitted[0].Relations.Should().Equal("owner", "admin", "member");
    }

    private sealed class FixedResultOp(bool value) : ICheckOp
    {
        public ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx,
            string entityType, string entityId, CancellationToken ct) => new(value);
        public string Describe() => $"Fixed({value})";
    }

    private sealed class RootReplacingRewriter(ICheckOp op) : IPlanRewriter
    {
        public PlanNode Rewrite(PlanNode root, Schema schema) => new PhysicalCheckNode(op);
    }

    [Fact]
    public async Task Executor_runs_a_PhysicalCheckNode_op_through_the_default_physical_executor()
    {
        // No tuples at all — a true result can only come from the injected op.
        var (_, schema, reader) = await Arrange(DocSchema, []);
        var snap = await Valtuutus.Core.Engines.SnapTokenUtils.ResolveLatest(reader, null, default);
        var ctx = new CheckRequestContext
            { SubjectType = "user", SubjectId = "u1", SnapToken = snap, Context = new Dictionary<string, object>() };
        var cache = new CheckPlanCache(schema, [new RootReplacingRewriter(new FixedResultOp(true))]);
        var executor = new CheckPlanExecutor(schema, cache)
            { Physical = new DefaultPhysicalExecutor(schema) { Reader = reader } };

        var results = await executor.ExecuteAsync(
            [new CheckRootRequest("doc", "1", "view", null, 10)], ctx, default);

        results[0].Should().BeTrue();
    }
}
