using System.Buffers;
using Valtuutus.Core.Data;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

internal readonly record struct CheckRootRequest(
    string EntityType, string EntityId, string Permission, string? SubjectRelation, int Depth);

// Pooled via CheckPlanExecutorPool (Task 16 Stage 4): schema/plans are DI singletons so they
// stay fixed for the pooled instance's whole lifetime; Physical wraps the per-request scoped
// reader, so it's set fresh on every Rent instead of being constructor-captured. Single-consumer
// driver loop: all frame/memo mutation happens on the loop; providers only touch the mailbox
// (thread-safe). That is why the memo structures below are plain (non-concurrent) collections.
internal sealed class CheckPlanExecutor(Schema schema, CheckPlanCache plans) : IOpCompletionSink
{
    private const int NoParent = -1;

    // Set by the pool wrapper before every ExecuteAsync call — never constructor-captured,
    // since the underlying reader is scoped and this instance may outlive many requests.
    internal IPhysicalExecutor Physical = null!;

    private CheckRequestContext _ctx = null!;
    private CancellationToken _ct;
    // Cleared (not reallocated) at the start of every ExecuteAsync. Safe because this instance
    // only ever re-enters ExecuteAsync after DrainStragglersAsync has fully drained it and the
    // pool has handed it out again — see CheckPlanExecutorPool for why that ordering holds.
    private readonly CompletionMailbox _mailbox = new();

    private Frame[] _frames = [];
    private int _frameCount;
    // Intrusive LIFO over Frame.NextReady — avoids a per-call Stack<int> + its backing-array
    // allocation by reusing storage already rented via ArrayPool<Frame>. -1 = empty.
    private int _readyHead = -1;

    // Ops that became ready during the current DrainReady pass, accumulated here instead of
    // being submitted one at a time — flushed as a single Physical.Submit call by FlushWave()
    // once the pass reaches quiescence (M1: wave-shaped driver loop). Same ArrayPool lifecycle
    // and lifetime rules as _frames (rent in ExecuteAsync's prologue, grow by doubling, return
    // only once _pendingOps reaches 0 — see ExecuteAsync/DrainStragglersAsync).
    private PendingOp[] _waveBuffer = [];
    private int _waveCount;

    private bool[] _results = [];
    private int _rootsPending;
    // Reused across calls on this pooled instance to avoid a heap allocation for the extremely
    // common single-root case (see ExecuteSingleAsync). Safe because ExecuteAsync only reads
    // `roots` synchronously in its prologue, before any await — nothing else can be writing to
    // this same pooled instance's buffer concurrently (see pool ownership sequencing above).
    private readonly CheckRootRequest[] _singleRootBuffer = new CheckRootRequest[1];
    // Total dispatched-but-not-yet-completed ops, independent of _rootsPending. A short-circuited
    // Union/Intersect can make _rootsPending hit 0 while sibling ops are still in flight — this
    // instance is NOT safe to return to the pool until _pendingOps also reaches 0 (see
    // DrainStragglersAsync), or a straggler could later post into a future request's mailbox
    // using a colliding frame token.
    private int _pendingOps;

    // Dynamic memo — the V1 CheckMemo equivalent, allocated lazily on first cross-boundary
    // resolution beyond the roots. Cleared (not reallocated) at the start of each ExecuteAsync
    // so a pooled instance reuses the same backing Dictionary/List across requests.
    private Dictionary<CheckMemoKey, int>? _memoIndex;
    private List<MemoEntry> _memoEntries = [];

    // Explain-only side channel. Indexed by the same frame index as _frames — populated only
    // when ExecuteAsync's `explain` parameter is true (Task 4), never touched otherwise, so
    // normal Check()/SubjectPermission() calls pay zero cost. Grows in lockstep with _frames
    // inside SpawnFrame (see Step 4 below).
    private bool _explain;
    private CheckNode?[] _explainNodes = [];
    // Parallel to _explainNodes, same index space, same "only when _explain" lifecycle. Holds the
    // OUTER node for a frame whose plan.Root needed V1's "always wrap, never reuse" treatment
    // (Union/Intersect/Negate at a plan root — see SpawnPlan) — _explainNodes[idx] in that case
    // holds the INNER auto-derived node instead (e.g. the "or"/"and" expression, or the negated
    // child), which becomes a CHILD of _wrapSelfNodes[idx] once this frame completes (see
    // CompleteFrame). Every other frame kind leaves this null; _explainNodes[idx] IS the real,
    // directly-attachable node for them, same as before this field existed.
    private CheckNode?[] _wrapSelfNodes = [];
    // Set once the single root frame completes (see CompleteFrame's generic attach logic,
    // Step 5). Explain is always single-root (mirrors ExecuteSingleAsync) — multi-root explain
    // is not supported and not needed, since ICheckEngine.Explain() takes one CheckRequest.
    private CheckNode? _explainRoot;

    // Kept intentionally lean (3 ints, no reference field) — used on every memo-wait
    // registration regardless of _explain, so its size directly affects List<Waiter>'s backing
    // array cost on the hot Check()/SubjectPermission() path. The explain-mode correlation lives
    // in a SEPARATE parallel list (MemoEntry.ExplainWaiters / MemoSlotState.ExplainWaiters,
    // below) instead of a field on this struct, so a plain (non-explaining) call never pays for
    // a wider struct here.
    internal readonly record struct Waiter(int Parent, int RootIndex, int ChildIndex);

    private struct MemoEntry
    {
        public bool Done;
        public bool Value;
        public List<Waiter>? Waiters;
        // Parallel to Waiters, same index for the same logical waiter — only ever populated when
        // _explain is true (see ResolveDynamic's memo-waiting branch). Kept separate from Waiter
        // itself so a plain, non-explaining call never allocates or touches this at all.
        public List<CheckNode?>? ExplainWaiters;
    }

    private struct Frame
    {
        public PlanNode Node;
        public int Parent;              // NoParent = reports into _results[RootIndex]
        public int RootIndex;
        public int ChildIndex;          // position among an expression parent's children; -1 elsewhere
        public byte State;              // node-type-specific step counter
        public int Pending;             // outstanding children (expression / fan-out frames)
        public bool Completed;
        public bool Result;
        public string EntityType;
        public string EntityId;
        public string? SubjectRelation; // binding V1 threads through CheckComputedUserSet
        public int Depth;
        public int MemoEntry;           // -1 = none; else fill on completion + wake waiters
        public MemoSlotState[]? Slots;  // owned by the plan-root frame, inherited by descendants
        public int NextReady;           // intrusive link for the _readyHead LIFO; undefined when not queued
    }

    // Slots are scoped to one spawned plan instance — a plan can be entered multiple times for
    // different entity ids, and slots must NOT be shared across entries. The array itself is
    // never resized, so holding a `ref` into it across a SpawnFrame call (which may grow/replace
    // the separate `_frames` array) is safe.
    internal struct MemoSlotState
    {
        public byte State;   // 0 empty, 1 in-flight, 2 done
        public bool Value;
        public List<Waiter>? Waiters;
        // Parallel to Waiters, same index for the same logical waiter — only ever populated when
        // _explain is true (see StepFrame's MemoNode case 1). Kept separate from Waiter itself so
        // a plain, non-explaining call never allocates or touches this at all.
        public List<CheckNode?>? ExplainWaiters;
    }

    private readonly record struct CheckMemoKey(
        string EntityType, string EntityId, string Permission, string? SubjectType, string? SubjectId);

    // memoizeRoots: false, ANALOGOUS to V1 CheckInternal's `memoize` parameter — Check()'s
    // single root can never be re-entered before it exists, so it skips the memo dictionary
    // entirely (mirrors V1 CheckEngine.Check() passing memoize: false to its one CheckInternal
    // call). SubjectPermission's N roots keep memoizing (V1 default) since they can
    // legitimately reference each other.
    public async Task<bool[]> ExecuteAsync(CheckRootRequest[] roots, CheckRequestContext ctx, CancellationToken ct,
        bool memoizeRoots = true, bool explain = false)
    {
        _ctx = ctx;
        _ct = ct;
        _explain = explain;
        _explainRoot = null;
        _mailbox.Clear();
        _frames = ArrayPool<Frame>.Shared.Rent(16);
        _frameCount = 0;
        _readyHead = -1;
        if (_explain)
        {
            _explainNodes = ArrayPool<CheckNode?>.Shared.Rent(16);
            Array.Clear(_explainNodes, 0, _explainNodes.Length);
            _wrapSelfNodes = ArrayPool<CheckNode?>.Shared.Rent(16);
            Array.Clear(_wrapSelfNodes, 0, _wrapSelfNodes.Length);
        }
        _waveBuffer = ArrayPool<PendingOp>.Shared.Rent(8);
        _waveCount = 0;
        _memoIndex?.Clear();
        _memoEntries.Clear();
        _results = new bool[roots.Length];
        _rootsPending = roots.Length;
        _pendingOps = 0;
        try
        {
            for (var i = 0; i < roots.Length; i++)
            {
                CheckNode? rootNode = _explain
                    ? new CheckNode
                    {
                        Type = CheckNodeType.Permission, Name = roots[i].Permission,
                        EntityType = roots[i].EntityType, EntityId = roots[i].EntityId,
                        SubjectType = _ctx.SubjectType, SubjectId = _ctx.SubjectId,
                    }
                    : null;
                ResolveDynamic(roots[i].EntityType, roots[i].EntityId, roots[i].Permission,
                    roots[i].SubjectRelation, roots[i].Depth, NoParent, i, memoizeRoots, rootNode);
            }

            DrainReady();
            while (_rootsPending > 0)
            {
                var completion = await _mailbox.DequeueAsync(ct).ConfigureAwait(false);
                if (completion.Error is not null)
                {
                    // Still counts as this token's op landing — must decrement _pendingOps like
                    // OnOpCompleted would, or a fault permanently over-counts outstanding ops and
                    // DrainStragglersAsync can never observe zero again.
                    // Deliberately does NOT call OnOpCompleted (or otherwise CompleteFrame/Notify
                    // this frame and its ancestors): that cascade would resolve the frame as if
                    // the op had legitimately returned false, letting a Union/Intersect ancestor
                    // short-circuit or resolve around the fault — silently masking it with a wrong
                    // answer instead of propagating the exception. Only the counter may move here.
                    _pendingOps--;
                    throw completion.Error;
                }
                OnOpCompleted(completion);
                DrainReady();
            }
            return _results;
        }
        finally
        {
            // If stragglers are still outstanding (short-circuited Union/Intersect), _frames
            // must stay alive — OnOpCompleted needs to read it to recognize each straggler as
            // stale. DrainStragglersAsync returns it (and the wave buffer) once they've all
            // landed. The wave buffer follows the same rule even though nothing reads it during
            // drain (FlushWave already reset it to empty) — DrainStragglersAsync's own DrainReady
            // calls can still append new ops to it (e.g. a TTU fan-out discovered while draining).
            if (_pendingOps == 0)
            {
                ArrayPool<Frame>.Shared.Return(_frames, clearArray: true);
                _frames = [];
                ArrayPool<PendingOp>.Shared.Return(_waveBuffer, clearArray: true);
                _waveBuffer = [];
                if (_explain)
                {
                    ArrayPool<CheckNode?>.Shared.Return(_explainNodes, clearArray: true);
                    _explainNodes = [];
                    ArrayPool<CheckNode?>.Shared.Return(_wrapSelfNodes, clearArray: true);
                    _wrapSelfNodes = [];
                }
            }
        }
    }

    // Avoids a heap-allocated CheckRootRequest[1] at every call site that only ever has one
    // root (CheckEngineV2.Check()) — ReadOnlySpan<T> can't be an async method parameter, so
    // ExecuteAsync itself can't take a span; this reuses a buffer already owned by the pooled
    // instance instead.
    public Task<bool[]> ExecuteSingleAsync(CheckRootRequest root, CheckRequestContext ctx, CancellationToken ct,
        bool memoizeRoots = true)
    {
        _singleRootBuffer[0] = root;
        return ExecuteAsync(_singleRootBuffer, ctx, ct, memoizeRoots);
    }

    // Explain always memoizes its (single) root — unlike Check()'s memoizeRoots: false, this
    // matches V1 CheckEngine.Explain()'s own behavior (it calls the node-taking CheckInternal
    // overload with its default memoize: true), not V1 Check()'s. There is exactly one root, so
    // nothing else in the same call could ever re-reference it anyway — this only affects whether
    // the root's own CheckMemoKey gets registered, which has no observable effect for a single
    // root, but keeps the two engines' internal behavior aligned.
    //
    // Unlike Check()/CheckEngineV2's fire-and-forget DrainAndReturn: a short-circuited
    // Union/Intersect can leave stragglers still in flight after ExecuteAsync already answered.
    // For a plain bool result that's harmless (a straggler can only mutate internal pooled
    // state, never the already-returned primitive). For explain it is NOT harmless — a straggler
    // that later actually completes still runs OnOpCompleted's normal Detail-setting code and
    // CompleteFrame's attach logic, which would overwrite a sibling's "skipped (...)" label (set
    // eagerly by MarkSiblingsSkipped, which only touches the Detail string, never the frame's
    // own Completed flag) with the straggler's real result — mutating a CheckNode tree the
    // caller may already be reading or serializing. So explain trades away Check()'s early-return
    // latency win for correctness: fully await every outstanding op before returning the tree.
    public async Task<(bool Result, CheckNode Root)> ExecuteExplainAsync(CheckRootRequest root,
        CheckRequestContext ctx, CancellationToken ct)
    {
        _singleRootBuffer[0] = root;
        var results = await ExecuteAsync(_singleRootBuffer, ctx, ct, memoizeRoots: true, explain: true);
        await DrainStragglersAsync();
        return (results[0], _explainRoot!);
    }

    // Drains any ops still in flight after ExecuteAsync has already answered the caller (a
    // short-circuited Union/Intersect doesn't wait for its losing siblings). Must run to
    // completion — and only then is this instance safe to return to the pool — otherwise a
    // straggler could later post into a future request's mailbox using a colliding frame token.
    // Deliberately not cancellable via the original request's token: this is pool-safety
    // bookkeeping for the executor itself, independent of whether the caller's request was
    // cancelled. Errors from a straggler are silently discarded — the caller already has their
    // answer, so a losing sibling's failure has nothing left to propagate to.
    internal async Task DrainStragglersAsync()
    {
        while (_pendingOps > 0)
        {
            var completion = await _mailbox.DequeueAsync(CancellationToken.None).ConfigureAwait(false);
            OnOpCompleted(completion);
            DrainReady();
        }
        ArrayPool<Frame>.Shared.Return(_frames, clearArray: true);
        _frames = [];
        ArrayPool<PendingOp>.Shared.Return(_waveBuffer, clearArray: true);
        _waveBuffer = [];
        if (_explain)
        {
            ArrayPool<CheckNode?>.Shared.Return(_explainNodes, clearArray: true);
            _explainNodes = [];
            ArrayPool<CheckNode?>.Shared.Return(_wrapSelfNodes, clearArray: true);
            _wrapSelfNodes = [];
        }
    }

    // ── IOpCompletionSink (called from provider threads) ────────────────────
    public void Complete(int token, bool result) => _mailbox.Post(new Completion(token, result, null, null));
    public void CompleteWithPayload(int token, object payload) => _mailbox.Post(new Completion(token, false, payload, null));
    public void Fail(int token, Exception error) => _mailbox.Post(new Completion(token, false, null, error));

    // ── scheduling ──────────────────────────────────────────────────────────
    private void DrainReady()
    {
        // Read the link BEFORE StepFrame runs: StepFrame may push more frames (mutating
        // _readyHead via nested SpawnFrame calls), so the next pointer must be captured first —
        // same ordering the old Stack<int>.Pop()-then-process pattern relied on.
        while (_readyHead >= 0)
        {
            var idx = _readyHead;
            _readyHead = _frames[idx].NextReady;
            StepFrame(idx);
        }
        // M1: every leaf op that became ready during this pass (including ones surfaced by
        // frames spawned mid-pass, e.g. a PlanRef re-entry) flushes as a single wave here.
        FlushWave();
    }

    // Maps a compiled PlanNode to the CheckNodeType/Name an explain tree shows for it — the V2
    // analogue of V1's CheckEngine.GetNodeInfo. `fallbackName` is used only where a PlanNode
    // carries no name of its own (ConstNode, and the `_` default for any node kind that should
    // never actually reach here structurally).
    private static (CheckNodeType Type, string Name) DescribeNode(PlanNode node, string fallbackName) => node switch
    {
        DirectRelationNode d => (CheckNodeType.Relation, d.Relation),
        AttributeTruthNode a => (CheckNodeType.Attribute, a.Attribute),
        AttributeExprNode e => (CheckNodeType.Function, e.Expr.FunctionName),
        TupleToUserSetNode t => (CheckNodeType.TupleToUserSet, $"{t.TuplesetRelation}.{t.ComputedRelation}"),
        PlanRefNode p => (CheckNodeType.Permission, p.Permission),
        UnionNode => (CheckNodeType.Expression, "or"),
        IntersectNode => (CheckNodeType.Expression, "and"),
        NegateNode => (CheckNodeType.Expression, "not"),
        PhysicalCheckNode p => (CheckNodeType.FusedOp, p.Op.Describe()),
        // MemoNode is never itself a visible node — peel through to what it wraps. Only reachable
        // here via SpawnPlan's explicit DescribeNode(plan.Root, ...) call (Task 3); the
        // StartExpression/StepFrame auto-derive path never calls DescribeNode on a bare MemoNode
        // (see the SpawnFrame change in Step 4 below).
        MemoNode m => DescribeNode(m.Child, fallbackName),
        _ => (CheckNodeType.Permission, fallbackName),
    };

    // Attaches `node` to the nearest real (non-pass-through) ancestor's children list, or sets it
    // as the explain root if there is no such ancestor. "Pass-through" ancestors are frames whose
    // _explainNodes slot is deliberately left null (the MemoNode 1st-reference wrapper, Task 3) —
    // walking past them means a shared subtree's real expansion attaches directly to whatever
    // referenced the MemoNode, never showing an extra wrapper level. `parent` is a FRAME index
    // (NoParent for a request root), matching every call site's own `parent`/`frame.Parent` value.
    //
    // Deliberately does NOT consult _wrapSelfNodes: a frame with a wrap set (SpawnPlan's
    // Union/Intersect case) always ALSO has a non-null _explainNodes[p] (the inner auto-derived
    // "and"/"or" node) — never a pass-through. A regular child spawned directly under that frame
    // (e.g. StartExpression's per-child spawns) must land on that inner node, exactly like this
    // walk already does, so the inner node accumulates its own children before CompleteFrame folds
    // it into the wrap node as ONE child (see CompleteFrame). Landing on the wrap node instead
    // would attach ordinary union/intersect members directly to the permission-level node,
    // skipping the "and"/"or" level entirely — the exact bug this file's wrap machinery exists to
    // avoid. The wrap node itself only ever reaches this walk explicitly, already selected by
    // CompleteFrame's own wrap-aware block.
    private void AttachOrSetRoot(CheckNode? node, int parent)
    {
        if (node is null) return;
        if (parent == NoParent) { _explainRoot ??= node; return; }
        var p = parent;
        while (p != NoParent && _explainNodes[p] is null) p = _frames[p].Parent;
        if (p == NoParent) _explainRoot ??= node;
        else _explainNodes[p]!._children.Add(node);
    }

    private int SpawnFrame(PlanNode node, string entityType, string entityId, string? subjectRelation,
        int depth, int parent, int rootIndex, int memoEntry = -1, MemoSlotState[]? slots = null,
        int childIndex = -1, CheckNode? explainNode = null, CheckNode? wrapSelfNode = null)
    {
        if (_frameCount == _frames.Length)
        {
            var bigger = ArrayPool<Frame>.Shared.Rent(_frames.Length * 2);
            Array.Copy(_frames, bigger, _frameCount);
            ArrayPool<Frame>.Shared.Return(_frames, clearArray: true);
            _frames = bigger;
            if (_explain)
            {
                var biggerNodes = ArrayPool<CheckNode?>.Shared.Rent(_frames.Length);
                Array.Copy(_explainNodes, biggerNodes, _frameCount);
                ArrayPool<CheckNode?>.Shared.Return(_explainNodes, clearArray: true);
                _explainNodes = biggerNodes;
                var biggerWrapNodes = ArrayPool<CheckNode?>.Shared.Rent(_frames.Length);
                Array.Copy(_wrapSelfNodes, biggerWrapNodes, _frameCount);
                ArrayPool<CheckNode?>.Shared.Return(_wrapSelfNodes, clearArray: true);
                _wrapSelfNodes = biggerWrapNodes;
            }
        }
        var idx = _frameCount++;
        _frames[idx] = new Frame
        {
            Node = node, Parent = parent, RootIndex = rootIndex, ChildIndex = childIndex, EntityType = entityType,
            EntityId = entityId, SubjectRelation = subjectRelation, Depth = depth, MemoEntry = memoEntry,
            Slots = slots, NextReady = _readyHead,
        };
        _readyHead = idx;
        if (_explain)
        {
            // Explicit explainNode (from ResolveDynamic/SpawnPlan, or a fan-out call site, Task 3)
            // always wins. Otherwise auto-derive from the PlanNode's own shape — EXCEPT a bare
            // MemoNode, which must stay null (a deliberate pass-through wrapper; StepFrame's
            // MemoNode case, Task 3, decides its real node lazily once it knows whether this is
            // the 1st or 2nd+ reference to the shared slot).
            _explainNodes[idx] = explainNode ?? (node is MemoNode ? null : MakeExplainNode(node, entityType, entityId));
            _wrapSelfNodes[idx] = wrapSelfNode;
        }
        return idx;
    }

    private CheckNode MakeExplainNode(PlanNode node, string entityType, string entityId)
    {
        var (type, name) = DescribeNode(node, entityType);
        return new CheckNode
        {
            Type = type, Name = name, EntityType = entityType, EntityId = entityId,
            SubjectType = _ctx.SubjectType, SubjectId = _ctx.SubjectId,
        };
    }

    // Exact analogue of V1 CheckInternal (CheckEngine.cs:211-276): the ONLY place depth is
    // charged, guards run, and the dynamic memo is consulted.
    private void ResolveDynamic(string entityType, string entityId, string permission,
        string? subjectRelation, int depth, int parent, int rootIndex, bool memoize = true,
        CheckNode? selfNode = null)
    {
        if (depth <= 0)
        {
            if (_explain && selfNode is not null)
            {
                selfNode.Detail = "depth limit reached";
                selfNode.Result = false;
                AttachOrSetRoot(selfNode, parent);
            }
            Notify(parent, rootIndex, false);
            return;
        }

        if (!string.IsNullOrEmpty(subjectRelation)
            && _ctx.SubjectType == entityType && _ctx.SubjectId == entityId && subjectRelation == permission)
        {
            if (_explain && selfNode is not null)
            {
                selfNode.Result = true;
                AttachOrSetRoot(selfNode, parent);
            }
            Notify(parent, rootIndex, true);
            return;
        }

        if (!string.IsNullOrEmpty(_ctx.SubjectType)
            && !schema.CanSubjectTypeReach(entityType, permission, _ctx.SubjectType))
        {
            if (_explain && selfNode is not null)
            {
                selfNode.Detail = "subject type cannot reach permission";
                selfNode.Result = false;
                AttachOrSetRoot(selfNode, parent);
            }
            Notify(parent, rootIndex, false);
            return;
        }

        if (!memoize)
        {
            SpawnPlan(entityType, entityId, permission, subjectRelation, depth, parent, rootIndex, memoEntry: -1, selfNode);
            return;
        }

        var key = new CheckMemoKey(entityType, entityId, permission, _ctx.SubjectType, _ctx.SubjectId);
        _memoIndex ??= new Dictionary<CheckMemoKey, int>();
        if (_memoIndex.TryGetValue(key, out var entryIdx))
        {
            var entry = _memoEntries[entryIdx];
            if (entry.Done)
            {
                ValtuutusMetrics.MemoHits.Add(1);
                if (_explain && selfNode is not null)
                {
                    selfNode.Detail = "memoized";
                    selfNode.Result = entry.Value;
                    AttachOrSetRoot(selfNode, parent);
                }
                Notify(parent, rootIndex, entry.Value);
                return;
            }
            if (_explain && selfNode is not null)
            {
                selfNode.Detail = "memoized";
                AttachOrSetRoot(selfNode, parent);
            }
            (entry.Waiters ??= []).Add(new Waiter(parent, rootIndex, -1));
            if (_explain) (entry.ExplainWaiters ??= []).Add(selfNode);
            _memoEntries[entryIdx] = entry;
            return;
        }

        _memoIndex[key] = _memoEntries.Count;
        var newEntryIdx = _memoEntries.Count;
        _memoEntries.Add(new MemoEntry());

        SpawnPlan(entityType, entityId, permission, subjectRelation, depth, parent, rootIndex, newEntryIdx, selfNode);
    }

    private void SpawnPlan(string entityType, string entityId, string permission, string? subjectRelation,
        int depth, int parent, int rootIndex, int memoEntry, CheckNode? selfNode)
    {
        var plan = plans.GetOrCompile(entityType, permission, _ctx.SubjectType);
        int newIdx;
        if (_explain && selfNode is not null && plan.Root is UnionNode or IntersectNode)
        {
            // V1 parity (CheckEngine.CheckExpressionWithWrapper): when the permission's own tree
            // IS a union/intersect, that combinator gets a separate "and"/"or" wrapper node as
            // selfNode's child — selfNode (the permission-level node) keeps its own Type/Name
            // untouched. Let SpawnFrame auto-derive the inner node normally (explainNode: null,
            // same auto-derive path every other spawn uses) and remember to wrap it into
            // selfNode once it resolves (wrapSelfNode:) — see CompleteFrame's wrap-aware attach
            // logic, MarkSiblingsSkipped, and AttachOrSetRoot, all of which must treat
            // _wrapSelfNodes[idx] (when set) as the real, attachable node instead of
            // _explainNodes[idx].
            newIdx = SpawnFrame(plan.Root, entityType, entityId, subjectRelation, depth - 1, parent, rootIndex, memoEntry,
                wrapSelfNode: selfNode);
        }
        else
        {
            // Negate (V1's NegateCheck) and every leaf shape (relation/attribute/function/TTU):
            // selfNode is reused directly as this frame's own explain node — no separate wrapper
            // frame needed, since AttachOrSetRoot already lands the negated child (or the leaf's
            // own detail) straight onto it. V1 parity split: leaf shapes DO get their Type
            // overwritten to describe themselves (CheckRelation/CheckAttribute/etc. all set
            // node.Type before doing their own work); NegateCheck is the one exception that
            // NEVER touches the passed node's Type at all — it only ever adds the negated
            // child as a plain child, so selfNode must keep whatever Type/Name its own caller
            // gave it (e.g. Type=Permission, Name="view").
            if (_explain && selfNode is not null && plan.Root is not NegateNode)
                selfNode.Type = DescribeNode(plan.Root, permission).Type;
            newIdx = SpawnFrame(plan.Root, entityType, entityId, subjectRelation, depth - 1, parent, rootIndex, memoEntry,
                explainNode: selfNode);
        }
        _frames[newIdx].Slots = plan.SlotCount > 0 ? new MemoSlotState[plan.SlotCount] : null;
    }

    private void Notify(int parent, int rootIndex, bool result, int childIndex = -1)
    {
        if (parent == NoParent)
        {
            _results[rootIndex] = result;
            _rootsPending--;
            return;
        }
        ChildCompleted(parent, result, childIndex);
    }

    // Called only when explain is on, right before a Union/Intersect short-circuits. Any sibling
    // already spawned under this parent (StartExpression spawns all children immediately) but not
    // yet Completed loses its chance to report a real Detail — mirrors V1's CheckExpressionChild,
    // which labels a sibling based on whether it had already completed at the exact instant the
    // combinator decided, not after waiting further.
    private void MarkSiblingsSkipped(int parentIdx, string detail)
    {
        // A direct Frame.Parent == parentIdx check is not enough: a union/intersect child that's
        // a bare relation/permission reference compiles as a PlanRefNode, whose StepFrame case
        // nulls out ITS OWN _explainNodes slot (pass-through) and re-parents the real explain
        // node one frame deeper (see StepFrame's PlanRefNode same-name branch) — exactly like a
        // MemoNode's first reference. That deeper frame is the one AttachOrSetRoot would actually
        // attach as parentIdx's child once it completes, so it's the one that needs the label
        // here too. Walk each not-yet-completed frame's OWN ancestor chain past any null
        // (pass-through) explain nodes, mirroring AttachOrSetRoot's walk, to find whether it
        // would land on parentIdx.
        for (var i = 0; i < _frameCount; i++)
        {
            if (_frames[i].Completed) continue;
            var p = _frames[i].Parent;
            while (p != NoParent && _explainNodes[p] is null) p = _frames[p].Parent;
            if (p != parentIdx) continue;
            // Prefer this candidate's own wrap node over its plain explain node: when i itself is
            // a plan root that needed SpawnPlan's wrap treatment, _explainNodes[i] is only the
            // INNER auto-derived combinator (e.g. delegate's own "or"), never the thing actually
            // attached as parentIdx's child — that's _wrapSelfNodes[i] (see CompleteFrame). The
            // wrap node is the one visible in the tree, so it's the one that needs the label.
            var n = _wrapSelfNodes[i] ?? _explainNodes[i];
            if (n is not null) n.Detail ??= detail;
        }
    }

    private void ChildCompleted(int parentIdx, bool childResult, int childIndex = -1)
    {
        ref var parent = ref _frames[parentIdx];
        if (parent.Completed) return; // stale — parent already short-circuited

        switch (parent.Node)
        {
            case UnionNode:
                if (childResult)
                {
                    // V1 parity (BoolTaskCombinator.cs:81, CheckEngine.cs:602): short-circuit
                    // counts only when a sibling was still unresolved; first-child counts when
                    // the deciding value came from child index 0. Expression frames only —
                    // deliberate divergence from V1, which also counted TTU fan-out
                    // short-circuits; V2 has no equivalent counter for that path.
                    if (parent.Pending > 1) ValtuutusMetrics.ShortCircuits.Add(1);
                    if (childIndex == 0) ValtuutusMetrics.FirstChildDecided.Add(1);
                    if (_explain) MarkSiblingsSkipped(parentIdx, "skipped (evaluation stopped after a success)");
                    CompleteFrame(parentIdx, true); return;
                }
                if (--parent.Pending == 0) CompleteFrame(parentIdx, false);
                break;

            case IntersectNode:
                if (!childResult)
                {
                    if (parent.Pending > 1) ValtuutusMetrics.ShortCircuits.Add(1);
                    if (childIndex == 0) ValtuutusMetrics.FirstChildDecided.Add(1);
                    if (_explain) MarkSiblingsSkipped(parentIdx, "skipped (evaluation stopped after a failure)");
                    CompleteFrame(parentIdx, false); return;
                }
                if (--parent.Pending == 0) CompleteFrame(parentIdx, true);
                break;

            case NegateNode:
                CompleteFrame(parentIdx, !childResult);
                break;

            case TupleToUserSetNode:
                if (childResult) { CompleteFrame(parentIdx, true); return; }
                if (--parent.Pending == 0) CompleteFrame(parentIdx, false);
                break;

            case DirectRelationNode:
                if (childResult) { CompleteFrame(parentIdx, true); return; }
                if (--parent.Pending == 0) CompleteFrame(parentIdx, false);
                break;

            case MemoNode m2:
            {
                var slots = parent.Slots!;
                ref var slot = ref slots[m2.SlotId];
                slot.State = 2;
                slot.Value = childResult;
                var waiters = slot.Waiters;
                var explainWaiters = slot.ExplainWaiters;
                slot.Waiters = null;
                slot.ExplainWaiters = null;
                CompleteFrame(parentIdx, childResult);
                if (waiters is not null)
                    for (var i = 0; i < waiters.Count; i++)
                    {
                        var w = waiters[i];
                        if (_explain && explainWaiters is not null)
                        {
                            var explainNode = explainWaiters[i];
                            if (explainNode is not null) explainNode.Result = childResult;
                        }
                        Notify(w.Parent, w.RootIndex, childResult, w.ChildIndex);
                    }
                break;
            }

            default:
                CompleteFrame(parentIdx, childResult);
                break;
        }
    }

    private void CompleteFrame(int idx, bool result)
    {
        ref var frame = ref _frames[idx];
        if (frame.Completed) return;
        frame.Completed = true;
        frame.Result = result;

        if (_explain)
        {
            var selfNode = _explainNodes[idx];
            var wrapSelfNode = _wrapSelfNodes[idx];
            if (wrapSelfNode is not null)
            {
                // Top-level Union/Intersect wrapper (SpawnPlan): `selfNode` here is the
                // auto-derived "and"/"or" node (this frame's own explain node); `wrapSelfNode` is
                // the permission-level node it belongs under. Thread the wrapper into
                // wrapSelfNode's children directly instead of the normal frame.Parent walk —
                // wrapSelfNode isn't registered in _explainNodes at any frame index — then attach
                // wrapSelfNode itself to the real ancestor.
                wrapSelfNode.Result = result;
                if (selfNode is not null)
                {
                    selfNode.Result = result;
                    wrapSelfNode._children.Add(selfNode);
                }
                AttachOrSetRoot(wrapSelfNode, frame.Parent);
            }
            else if (selfNode is not null)
            {
                selfNode.Result = result;
                AttachOrSetRoot(selfNode, frame.Parent);
            }
        }

        if (frame.MemoEntry >= 0)
        {
            var entry = _memoEntries[frame.MemoEntry];
            entry.Done = true;
            entry.Value = result;
            var waiters = entry.Waiters;
            var explainWaiters = entry.ExplainWaiters;
            entry.Waiters = null;
            entry.ExplainWaiters = null;
            _memoEntries[frame.MemoEntry] = entry;
            if (waiters is not null)
                for (var i = 0; i < waiters.Count; i++)
                {
                    var w = waiters[i];
                    if (_explain && explainWaiters is not null)
                    {
                        var explainNode = explainWaiters[i];
                        if (explainNode is not null) explainNode.Result = result;
                    }
                    Notify(w.Parent, w.RootIndex, result, w.ChildIndex);
                }
        }

        Notify(frame.Parent, frame.RootIndex, result, frame.ChildIndex);
    }

    // ── frame stepping ──────────────────────────────────────────────────────
    private void StepFrame(int idx)
    {
        ref var frame = ref _frames[idx];
        if (frame.Completed) return;

        switch (frame.Node)
        {
            case ConstNode c:
                if (_explain) { var n = _explainNodes[idx]; if (n is not null) n.Detail = $"const={c.Value}"; }
                CompleteFrame(idx, c.Value);
                break;

            case PlanRefNode p:
            {
                CheckNode? reentryNode = null;
                if (_explain)
                {
                    var existing = _explainNodes[idx];
                    if (existing is not null && existing.Name == p.Permission)
                    {
                        // V1 parity (CheckComputedUserSet's "same name -> reuse" branch): this
                        // frame's own node was auto-derived at spawn time (SpawnFrame's
                        // MakeExplainNode/DescribeNode) and already has the right name — reuse it
                        // directly instead of wrapping it in a redundant child. Transfer it into a
                        // pass-through (same convention as MemoNode's 1st reference, see
                        // StepFrame's MemoNode case default branch): null out this frame's own
                        // slot so only the re-entered plan's own completion attaches this object
                        // (via AttachOrSetRoot's existing walk-up-past-null logic), exactly once,
                        // to this frame's real ancestor — never to this frame's own idx (that was
                        // the earlier self-reference bug).
                        _explainNodes[idx] = null;
                        reentryNode = existing;
                    }
                    else
                    {
                        // Different name (e.g. a bare top-level alias like
                        // `permission view := owner;`, where this frame's own node is Name="view"
                        // but the re-entry targets "owner") — V1's "different name -> create
                        // child" branch: a genuinely fresh node, attached as ONE child of this
                        // frame's own (unchanged) node once it resolves.
                        reentryNode = new CheckNode
                        {
                            Type = CheckNodeType.Permission, Name = p.Permission,
                            EntityType = frame.EntityType, EntityId = frame.EntityId,
                            SubjectType = _ctx.SubjectType, SubjectId = _ctx.SubjectId,
                        };
                    }
                }
                ResolveDynamic(frame.EntityType, frame.EntityId, p.Permission,
                    frame.SubjectRelation, frame.Depth + 1, idx, frame.RootIndex, selfNode: reentryNode);
                // +1: this frame's Depth was already charged by the ResolveDynamic that spawned
                // this plan; the re-entry must charge exactly one more (V1: nested CheckInternal
                // receives the parent's nextDepth and decrements again).
                break;
            }

            case DirectRelationNode d:
                SubmitOp(new PendingOp
                {
                    Token = idx, Kind = OpKind.HasDirectRelation,
                    EntityType = frame.EntityType, EntityId = frame.EntityId, Relation = d.Relation
                });
                break;

            case AttributeTruthNode a:
                SubmitOp(new PendingOp
                {
                    Token = idx, Kind = OpKind.HasTrueBoolAttribute,
                    EntityType = frame.EntityType, EntityId = frame.EntityId, Relation = a.Attribute
                });
                break;

            case UnionNode u:
                StartExpression(idx, u.Children, isUnion: true);
                break;

            case IntersectNode n:
                StartExpression(idx, n.Children, isUnion: false);
                break;

            case NegateNode neg:
            {
                frame.Pending = 1;
                SpawnFrame(neg.Child, frame.EntityType, frame.EntityId, frame.SubjectRelation,
                    frame.Depth, idx, frame.RootIndex, slots: frame.Slots);
                break;
            }

            case TupleToUserSetNode t:
                StepTupleToUserSet(idx, t);
                break;

            case AttributeExprNode e:
                SubmitOp(new PendingOp
                {
                    Token = idx, Kind = OpKind.AttributeExpr,
                    EntityType = frame.EntityType, EntityId = frame.EntityId, Expr = e.Expr
                });
                break;

            case PhysicalCheckNode p:
                SubmitOp(new PendingOp
                {
                    Token = idx, Kind = OpKind.CheckOp,
                    EntityType = frame.EntityType, EntityId = frame.EntityId, Op = p.Op
                });
                break;

            case MemoNode m:
            {
                var slots = frame.Slots!;
                ref var slot = ref slots[m.SlotId];
                switch (slot.State)
                {
                    case 2:
                        if (_explain)
                        {
                            var (type, name) = DescribeNode(m.Child, frame.EntityType);
                            _explainNodes[idx] = new CheckNode
                            {
                                Type = type, Name = name, EntityType = frame.EntityType, EntityId = frame.EntityId,
                                SubjectType = _ctx.SubjectType, SubjectId = _ctx.SubjectId,
                                Result = slot.Value, Detail = "memoized (shared subtree)",
                            };
                        }
                        CompleteFrame(idx, slot.Value);
                        break;
                    case 1:
                        (slot.Waiters ??= []).Add(new Waiter(frame.Parent, frame.RootIndex, frame.ChildIndex));
                        if (_explain)
                        {
                            var (type, name) = DescribeNode(m.Child, frame.EntityType);
                            var waiterNode = new CheckNode
                            {
                                Type = type, Name = name, EntityType = frame.EntityType, EntityId = frame.EntityId,
                                SubjectType = _ctx.SubjectType, SubjectId = _ctx.SubjectId,
                                Detail = "memoized (shared subtree)",
                            };
                            AttachOrSetRoot(waiterNode, frame.Parent);
                            (slot.ExplainWaiters ??= []).Add(waiterNode);
                        }
                        // This frame dissolves into a waiter registration; mark completed so
                        // stale bookkeeping never routes to it again.
                        frame.Completed = true;
                        break;
                    default:
                        slot.State = 1;
                        frame.Pending = 1;
                        // _explainNodes[idx] stays null here by design — a pass-through wrapper.
                        // The real expansion is m.Child, spawned below; when it completes,
                        // CompleteFrame's AttachOrSetRoot walks past this null slot straight to
                        // this frame's own parent, so the shared subtree's first expansion never
                        // shows an extra wrapper level.
                        SpawnFrame(m.Child, frame.EntityType, frame.EntityId, frame.SubjectRelation,
                            frame.Depth, idx, frame.RootIndex, slots: frame.Slots);
                        break;
                }
                break;
            }

            default:
                throw new InvalidOperationException($"Executor cannot step node type {frame.Node.GetType().Name} yet");
        }
    }

    private void StepTupleToUserSet(int idx, TupleToUserSetNode t)
    {
        ref var frame = ref _frames[idx];

        // Schema-static eligibility is precomputed by PlanCompiler.PruneAndFold. frame.Depth > 0
        // stays a runtime check — CheckRequest.Depth is a per-request recursion budget, not
        // schema-derivable.
        if (t.FastPathSubEntityType is not null && frame.Depth > 0)
        {
            frame.State = 1;
            SubmitOp(new PendingOp
            {
                Token = idx, Kind = OpKind.TtuFastPath,
                EntityType = frame.EntityType, EntityId = frame.EntityId,
                Relation = t.TuplesetRelation, SubEntityType = t.FastPathSubEntityType,
                ComputedRelation = t.ComputedRelation
            });
            return;
        }

        frame.State = 2;
        SubmitOp(new PendingOp
        {
            Token = idx, Kind = OpKind.GetRelations,
            EntityType = frame.EntityType, EntityId = frame.EntityId, Relation = t.TuplesetRelation
        });
    }

    private void OnTupleToUserSetExpanded(int idx, TupleToUserSetNode t, PooledList<RelationTuple> relations)
    {
        using var _ = relations;
        ref var frame = ref _frames[idx];

        if (relations.Count == 0)
        {
            // ??= — see OnOpCompleted's DirectRelationNode case for why (same MarkSiblingsSkipped race).
            if (_explain) { var n = _explainNodes[idx]; if (n is not null) n.Detail ??= "no matching tuples"; }
            CompleteFrame(idx, false); return;
        }

        if (relations.Count > 1)
        {
            // Batch fast path — CheckEngine.cs:798-819.
            var firstSubjectType = relations.AsSpan()[0].SubjectType;
            var allSame = true;
            foreach (ref readonly var r in relations.AsSpan())
                if (r.SubjectType != firstSubjectType) { allSame = false; break; }

            if (allSame
                && schema.GetRelationType(firstSubjectType, t.ComputedRelation) == RelationType.DirectRelation
                && !schema.GetRelation(firstSubjectType, t.ComputedRelation).HasSubRelationPaths)
            {
                var entityIds = new string[relations.Count];
                for (var i = 0; i < relations.Count; i++)
                    entityIds[i] = relations[i].SubjectId;
                frame.State = 3;
                SubmitOp(new PendingOp
                {
                    Token = idx, Kind = OpKind.HasAnyDirectRelation,
                    EntityType = firstSubjectType, EntityId = "", Relation = t.ComputedRelation,
                    EntityIds = entityIds
                });
                return;
            }
        }

        frame.State = 3;
        frame.Pending = relations.Count;
        // Captured before the loop: ResolveDynamic can spawn a frame and grow/replace _frames
        // mid-loop, which would dangle this `ref frame` — never read through it again inside.
        var computedRelation = t.ComputedRelation;
        var depth = frame.Depth;
        var rootIndex = frame.RootIndex;
        var explainParent = idx;
        // Fan-out children behave as a Union (V1 shortCircuitOn: true).
        foreach (ref readonly var rel in relations.AsSpan())
        {
            CheckNode? childNode = _explain
                ? new CheckNode
                {
                    Type = CheckNodeType.Permission, Name = computedRelation,
                    EntityType = rel.SubjectType, EntityId = rel.SubjectId,
                    SubjectType = _ctx.SubjectType, SubjectId = _ctx.SubjectId,
                }
                : null;
            ResolveDynamic(rel.SubjectType, rel.SubjectId, computedRelation, rel.SubjectRelation,
                depth, explainParent, rootIndex, selfNode: childNode);
        }
    }

    private void OnIndirectRelationsFetched(int idx, PooledList<RelationTuple> relations)
    {
        using var _ = relations;
        if (relations.Count == 0)
        {
            // ??= — see OnOpCompleted's DirectRelationNode case for why (same MarkSiblingsSkipped race).
            if (_explain) { var n = _explainNodes[idx]; if (n is not null) n.Detail ??= "no matching tuple"; }
            CompleteFrame(idx, false); return;
        }

        _frames[idx].Pending = relations.Count;
        var depth = _frames[idx].Depth;
        var rootIndex = _frames[idx].RootIndex;
        // Re-indexes _frames[idx] each iteration (not a held `ref`), so this is safe even if
        // ResolveDynamic spawns a frame that grows/replaces the array mid-loop.
        foreach (ref readonly var rel in relations.AsSpan())
        {
            CheckNode? childNode = _explain
                ? new CheckNode
                {
                    Type = CheckNodeType.Permission, Name = rel.SubjectRelation ?? rel.SubjectType,
                    EntityType = rel.SubjectType, EntityId = rel.SubjectId,
                    SubjectType = _ctx.SubjectType, SubjectId = _ctx.SubjectId,
                }
                : null;
            ResolveDynamic(rel.SubjectType, rel.SubjectId, rel.SubjectRelation!, null,
                depth, idx, rootIndex, selfNode: childNode);
        }
    }

    private void StartExpression(int idx, System.Collections.Immutable.ImmutableArray<PlanNode> children, bool isUnion)
    {
        ref var frame = ref _frames[idx];
        frame.Pending = children.Length;
        // Captured before the loop: SpawnFrame can grow/replace _frames mid-loop, which would
        // dangle this `ref frame` — never read through it again once a spawn call may have fired.
        var entityType = frame.EntityType;
        var entityId = frame.EntityId;
        var subjectRelation = frame.SubjectRelation;
        var depth = frame.Depth;
        var rootIndex = frame.RootIndex;
        var slots = frame.Slots;
        ValtuutusMetrics.ExpressionNodes.Add(1);
        // All children spawn immediately — V1 parity (parallel siblings). SequentialFirst is a
        // later, metrics-gated policy applied here per plan annotation.
        for (var i = 0; i < children.Length; i++)
            SpawnFrame(children[i], entityType, entityId, subjectRelation, depth, idx, rootIndex,
                slots: slots, childIndex: i);
    }

    // Appends to the current wave instead of submitting immediately (M1) — FlushWave() (called
    // once per DrainReady pass, see below) is the only place that actually calls Physical.Submit.
    private void SubmitOp(in PendingOp op)
    {
        _pendingOps++;
        if (_waveCount == _waveBuffer.Length)
        {
            var bigger = ArrayPool<PendingOp>.Shared.Rent(_waveBuffer.Length * 2);
            Array.Copy(_waveBuffer, bigger, _waveCount);
            ArrayPool<PendingOp>.Shared.Return(_waveBuffer, clearArray: true);
            _waveBuffer = bigger;
        }
        _waveBuffer[_waveCount++] = op;
    }

    // Submits everything accumulated by SubmitOp since the last flush, then resets for the next
    // wave. Called once per DrainReady pass — see DrainReady below. A no-op when nothing became
    // ready as a leaf op this pass (e.g. a pass that only resolved memo hits or PlanRef re-entries).
    private void FlushWave()
    {
        if (_waveCount == 0) return;

        // Once every root already has its final answer (_rootsPending == 0 — true for the tail
        // of the main loop once the last root decides, and always true throughout
        // DrainStragglersAsync), anything still sitting in the wave buffer is work discovered
        // after the caller already has _results back. Drop it instead of submitting it: nobody
        // will ever read the answer, so the real provider round trip is pure waste. _pendingOps
        // is repaid here exactly as OnOpCompleted would repay it on a real completion, so
        // DrainStragglersAsync's `while (_pendingOps > 0)` still terminates correctly — usually
        // faster, since a dropped op never needs an async round trip through the mailbox at all.
        //
        // Scope note: this only catches the case where EVERY root already decided. A multi-root
        // SubjectPermission call where one root short-circuits while others are still pending
        // does not get this benefit for that root's own stale descendants yet (_rootsPending
        // isn't 0 until every root is done) — that needs per-branch ancestor tracking, a larger
        // separate change. A cancelled or faulted request also never hits this path
        // (cancellation/fault propagate via the mailbox, not via Notify, so they never force
        // _rootsPending to 0) — CheckEngineV2 abandons those executors instead of pooling them
        // regardless, so optimizing their drain has little value.
        if (_rootsPending == 0)
        {
            _pendingOps -= _waveCount;
            _waveCount = 0;
            return;
        }

        ValtuutusMetrics.WaveOps.Record(_waveCount);

        // M3: how many ops in this wave share an OpKind with at least one sibling — stackalloc,
        // never heap-allocated, since this runs on every wave flush.
        Span<int> kindCounts = stackalloc int[OpKindMeta.OpKindCount];
        for (var i = 0; i < _waveCount; i++) kindCounts[(byte)_waveBuffer[i].Kind]++;
        for (var i = 0; i < _waveCount; i++)
            if (kindCounts[(byte)_waveBuffer[i].Kind] >= 2) ValtuutusMetrics.WaveSameKindOps.Add(1);

        Physical.Submit(_waveBuffer.AsSpan(0, _waveCount), _ctx, this, _ct);
        _waveCount = 0;
    }

    // ── op completion routing ───────────────────────────────────────────────
    private void OnOpCompleted(in Completion completion)
    {
        _pendingOps--;
        var idx = completion.Token;
        ref var frame = ref _frames[idx];
        if (frame.Completed)
        {
            (completion.Payload as IDisposable)?.Dispose();
            return; // stale — most commonly a short-circuit straggler (see DrainStragglersAsync)
        }

        switch (frame.Node)
        {
            case DirectRelationNode d:
                if (completion.Payload is PooledList<RelationTuple> indirect)
                {
                    OnIndirectRelationsFetched(idx, indirect);
                }
                else if (completion.Result || !d.HasSubRelationPaths)
                {
                    if (_explain)
                    {
                        var n = _explainNodes[idx];
                        // ??=, not =: a sibling still in flight when a Union/Intersect
                        // short-circuits gets eagerly labeled by MarkSiblingsSkipped before this
                        // op's own (already-submitted, uncancellable) completion lands — that
                        // label must win, not get clobbered by the real detail arriving after.
                        if (n is not null) n.Detail ??= completion.Result ? "direct tuple" : "no matching tuple";
                    }
                    CompleteFrame(idx, completion.Result);
                }
                else
                {
                    _frames[idx].State = 1;
                    SubmitOp(new PendingOp
                    {
                        Token = idx, Kind = OpKind.GetIndirectRelations,
                        EntityType = _frames[idx].EntityType, EntityId = _frames[idx].EntityId,
                        Relation = d.Relation
                    });
                }
                break;

            case AttributeTruthNode:
                if (_explain)
                {
                    var n = _explainNodes[idx];
                    // ??= — see the DirectRelationNode case above for why.
                    if (n is not null) n.Detail ??= $"attribute={completion.Result}";
                }
                CompleteFrame(idx, completion.Result);
                break;

            case TupleToUserSetNode t:
                if (completion.Payload is PooledList<RelationTuple> rels)
                {
                    OnTupleToUserSetExpanded(idx, t, rels);
                }
                else
                {
                    if (_explain)
                    {
                        var n = _explainNodes[idx];
                        // ??= — see the DirectRelationNode case above for why.
                        if (n is not null)
                            n.Detail ??= frame.State == 1
                                ? (completion.Result ? "fast-path: direct join found" : "fast-path: no join found")
                                : (completion.Result ? "batch: direct relation found" : "batch: no direct relation");
                    }
                    CompleteFrame(idx, completion.Result); // fast path or batch result
                }
                break;

            case AttributeExprNode:
                if (_explain)
                {
                    var n = _explainNodes[idx];
                    // ??= — see the DirectRelationNode case above for why.
                    if (n is not null) n.Detail ??= $"fn result={completion.Result}";
                }
                CompleteFrame(idx, completion.Result);
                break;

            case PhysicalCheckNode:
                if (_explain)
                {
                    var n = _explainNodes[idx];
                    // ??= — see the DirectRelationNode case above for why.
                    if (n is not null) n.Detail ??= $"result={completion.Result}";
                }
                CompleteFrame(idx, completion.Result);
                break;

            default:
                throw new InvalidOperationException($"Unexpected op completion for node {frame.Node.GetType().Name}");
        }
    }
}
