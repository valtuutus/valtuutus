using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;

namespace Valtuutus.Benchmarks;

public abstract class BenchmarkBase
{
    private const string OrgId          = "5171869f-b4e4-ca9a-b800-5e1dab069a26";
    private const string ProjectId       = "e4010d7b-cea1-94c6-2232-e1f9ae557272";
    private const string UserId          = "3fca4119-3bda-4370-13cd-a3d317459c73";
    private const string DiamondFolderId = "cccccccc-cccc-cccc-cccc-cccccccccc01";

    protected ICheckEngine _checkEngine = null!;
    protected ILookupEntityEngine _lookupEntityEngine = null!;

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
}
