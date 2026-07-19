using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Engines.LookupSubject;

namespace Valtuutus.Benchmarks;

public abstract class BenchmarkBase
{
    private const string OrgId          = "5171869f-b4e4-ca9a-b800-5e1dab069a26";
    private const string ProjectId       = "e4010d7b-cea1-94c6-2232-e1f9ae557272";
    private const string UserId          = "3fca4119-3bda-4370-13cd-a3d317459c73";
    private const string DiamondFolderId = "cccccccc-cccc-cccc-cccc-cccccccccc01";
    private const string FanoutProjectId = "dddddddd-dddd-dddd-dddd-dddddddddd01";
    private const string SiblingBatchTeamId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeee04";
    private const string MissUserId = "ffffffff-ffff-ffff-ffff-ffffffffff01"; // never seeded — deterministic miss

    protected ICheckEngine _checkEngine = null!;
    protected ILookupEntityEngine _lookupEntityEngine = null!;
    protected ILookupSubjectEngine _lookupSubjectEngine = null!;

    // ── existing scenarios ──────────────────────────────────────────────────

    [Benchmark(Baseline = true), BenchmarkCategory("Check_Simple")]
    public async Task<bool> Check_Simple()
        => await _checkEngine.Check(new()
        {
            Permission = "admin", EntityType = "organization", EntityId = OrgId,
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    [Benchmark(Baseline = true), BenchmarkCategory("Check_Complex")]
    public async Task<bool> Check_Complex()
        => await _checkEngine.Check(new()
        {
            Permission = "edit", EntityType = "project", EntityId = ProjectId,
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    /// <summary>
    /// Dead-branch scenario: "access := admin or bot", where "bot" is a relation only
    /// reachable by a "service_account" subject. Checking with SubjectType="user" makes the
    /// "bot" branch statically unreachable — with subject-type pruning it's skipped entirely
    /// (no task spawned, no DB round-trip); without it, both branches are evaluated.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("Check_Prune")]
    public async Task<bool> Check_Prune()
        => await _checkEngine.Check(new()
        {
            Permission = "access", EntityType = "organization", EntityId = OrgId,
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    [Benchmark(Baseline = true), BenchmarkCategory("SubjectPermission")]
    public async Task<Dictionary<string, bool>> SubjectPermission()
        => await _checkEngine.SubjectPermission(new()
        {
            EntityType = "project", EntityId = ProjectId,
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    [Benchmark(Baseline = true), BenchmarkCategory("LookupEntity")]
    public async Task<LookupEntityPage> LookupEntity()
        => await _lookupEntityEngine.LookupEntity(new()
        {
            Permission = "edit", EntityType = "project",
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    /// <summary>
    /// Same as LookupEntity but scoped to a single organization via a DB-level JOIN.
    /// Answers "which projects in org X can user Y edit?" — the primary use case for
    /// scoped lookup (e.g. GET /orgs/{id}/projects).
    /// Expected to be faster than the unscoped variant because the JOIN pre-filters
    /// the candidate set to ~100 projects instead of all 5000.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupEntity_Scoped")]
    public async Task<LookupEntityPage> LookupEntity_Scoped()
        => await _lookupEntityEngine.LookupEntity(new()
        {
            Permission = "edit", EntityType = "project",
            SubjectType = "user", SubjectId = UserId,
            Scope = new EntityScope("org", "organization", OrgId)
        }, CancellationToken.None);

    /// <summary>
    /// team.invite := org.admin and (owner or member) — the inner Union(owner, member) is 2 live
    /// batchable direct-@user-relation siblings, no schema/seed changes needed (existing team seed
    /// data already has owner/member tuples at 1000-team scale). Exercises the sibling-batching
    /// path added in #237: one GetRelationsWithSubjectsIdsMultiRelation call instead of 2 separate
    /// LookupRelationLeaf round trips for the owner/member branch.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupEntity_SiblingBatch")]
    public async Task<LookupEntityPage> LookupEntity_SiblingBatch()
        => await _lookupEntityEngine.LookupEntity(new()
        {
            Permission = "invite", EntityType = "team",
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    /// <summary>
    /// team.constrained_sibling_batch := isActive(active) and owner and member — an Intersect
    /// mixing one attribute-expression leaf with 2 batchable direct-@user-relation siblings
    /// (owner, member). Exercises LookupIntersectionConstrained's non-attribute-child loop
    /// batching added in #243 (deferred out of #237's generic-path-only scope).
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupEntity_ConstrainedSiblingBatch")]
    public async Task<LookupEntityPage> LookupEntity_ConstrainedSiblingBatch()
        => await _lookupEntityEngine.LookupEntity(new()
        {
            Permission = "constrained_sibling_batch", EntityType = "team",
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    /// <summary>
    /// team.negate_sibling_batch := owner and member and not(banned) — an Intersect mixing 2
    /// batchable direct-@user-relation siblings (owner, member) with a Negate child. Exercises
    /// LookupIntersectionWithNegate's positive-child loop batching added in #243.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupEntity_NegateSiblingBatch")]
    public async Task<LookupEntityPage> LookupEntity_NegateSiblingBatch()
        => await _lookupEntityEngine.LookupEntity(new()
        {
            Permission = "negate_sibling_batch", EntityType = "team",
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    /// <summary>
    /// project.reviewers has two indirect variants (team#member, group#member) — neither is
    /// the "user" subject type directly, so both require a dependent query and can fire
    /// concurrently instead of serializing.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupSubject")]
    public async Task<HashSet<string>> LookupSubject()
        => await _lookupSubjectEngine.Lookup(new()
        {
            Permission = "reviewers", EntityType = "project", EntityId = FanoutProjectId,
            SubjectType = "user"
        }, CancellationToken.None);

    /// <summary>
    /// team.invite := org.admin and (owner or member) — LookupSubject counterpart to
    /// LookupEntity_SiblingBatch. The inner Union(owner, member) is 2 live batchable
    /// direct-@user-relation siblings; deterministic seed data (SiblingBatchTeamId) gives
    /// LookupSubject a fixed EntityId to query "who can invite here". Exercises the
    /// sibling-batching path added for LookupSubjectEngine (#238): one
    /// GetRelationsWithEntityIdsMultiRelation call instead of 2 separate LookupRelationLeaf
    /// round trips for the owner/member branch.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupSubject_SiblingBatch")]
    public async Task<HashSet<string>> LookupSubject_SiblingBatch()
        => await _lookupSubjectEngine.Lookup(new()
        {
            Permission = "invite", EntityType = "team", EntityId = SiblingBatchTeamId,
            SubjectType = "user"
        }, CancellationToken.None);

    // ── diamond scenarios ───────────────────────────────────────────────────

    /// <summary>
    /// TTU diamond: folder.view := owner or editor, both @group#member pointing to the
    /// same group. LookupEntityEngine has no general-purpose memo (only AttributeCache, which
    /// is attribute-fetch-only) — the speedup here instead comes from GetRelationsJoined/
    /// JoinedLookup (LookupEntityEngine.cs), which collapses each branch's two-hop
    /// dependent-then-main traversal into a single joined query.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupEntity_Diamond")]
    public async Task<LookupEntityPage> LookupEntity_Diamond()
        => await _lookupEntityEngine.LookupEntity(new()
        {
            Permission = "view", EntityType = "folder",
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    /// <summary>
    /// Same TTU diamond as LookupEntity_Diamond, reverse direction: "who can view folder X"
    /// instead of "which folders can user Y view". LookupSubjectEngine has no two-hop JOIN
    /// collapse yet (tracked as a follow-up) — this establishes the baseline that fix will
    /// improve on.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("LookupSubject_Diamond")]
    public async Task<HashSet<string>> LookupSubject_Diamond()
        => await _lookupSubjectEngine.Lookup(new()
        {
            Permission = "view", EntityType = "folder", EntityId = DiamondFolderId,
            SubjectType = "user"
        }, CancellationToken.None);

    /// <summary>
    /// CheckMemo diamond: folder.admin := owner and editor, both sides check the same
    /// group#member path. Second check is a memo hit with the optimization.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("Check_Diamond")]
    public async Task<bool> Check_Diamond()
        => await _checkEngine.Check(new()
        {
            Permission = "admin", EntityType = "folder", EntityId = DiamondFolderId,
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    /// <summary>
    /// SubjectPermission diamond: checks all permissions (view + admin) on a folder where
    /// both permissions share group#member lookups. CheckMemo fires across permission checks.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("SubjectPermission_Diamond")]
    public async Task<Dictionary<string, bool>> SubjectPermission_Diamond()
        => await _checkEngine.SubjectPermission(new()
        {
            EntityType = "folder", EntityId = DiamondFolderId,
            SubjectType = "user", SubjectId = UserId
        }, CancellationToken.None);

    /// <summary>
    /// Userset miss path: folder.view := owner or editor, both @group#member, and the subject
    /// belongs to no group. Forces the full expansion per relation (HasDirectRelation +
    /// GetIndirectRelations + fan-out group member checks). Baseline for the planned userset
    /// 2-hop join rewrite (R2), which should collapse this to a single round trip.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("Check_UsersetMiss")]
    public async Task<bool> Check_UsersetMiss()
        => await _checkEngine.Check(new()
        {
            Permission = "view", EntityType = "folder", EntityId = DiamondFolderId,
            SubjectType = "user", SubjectId = MissUserId
        }, CancellationToken.None);

    /// <summary>
    /// TTU + direct union miss path: team.edit := org.admin or owner, subject with no tuples
    /// at all. Two round trips today (TTU fast-path join + direct EXISTS). Baseline for
    /// boolean-combination fusion (R1), which should answer it with one fused
    /// EXISTS-OR-EXISTS statement.
    /// </summary>
    [Benchmark(Baseline = true), BenchmarkCategory("Check_UnionTtuDirect")]
    public async Task<bool> Check_UnionTtuDirect()
        => await _checkEngine.Check(new()
        {
            Permission = "edit", EntityType = "team", EntityId = SiblingBatchTeamId,
            SubjectType = "user", SubjectId = MissUserId
        }, CancellationToken.None);
}
