using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class CheckEngineV2ExplainSpecs : BaseCheckEngineV2ExplainSpecs
{
    public CheckEngineV2ExplainSpecs(InMemoryFixture fixture) : base(fixture) { }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddInMemory();

    [Fact]
    public async Task Explain_UnionExpression_SkippedSiblingHasWording()
    {
        // read := viewer or editor or admin — alice is admin, so the union short-circuits true
        // as soon as one child decides it; whichever sibling hadn't completed yet gets the
        // skipped wording. InMemory-only (see rationale above) — do NOT move this to the shared
        // BaseCheckEngineV2ExplainSpecs/BaseCheckEngineV2RelationalExplainSpecs base.
        //
        // alice must hold the LAST-declared child ("admin"), not the first ("viewer"): InMemory's
        // driver (CheckPlanExecutor._readyHead) is an intrusive LIFO stack, so StartExpression's
        // children are stepped — and their ops submitted/completed, since DefaultPhysicalExecutor
        // completes synchronously in submission order — in REVERSE declaration order. A
        // first-declared decider would therefore always complete LAST, after every sibling had
        // already finished, leaving nothing for MarkSiblingsSkipped to mark. A last-declared
        // decider completes FIRST, while the earlier-declared siblings are still genuinely
        // pending.
        const string schema = """
            entity user {}
            entity resource {
                relation viewer @user;
                relation editor @user;
                relation admin @user;
                permission read := viewer or editor or admin;
            }
            """;
        var engine = await CreateEngine(
            [new RelationTuple("resource", "v2-expl-skip1", "admin", "user", "alice")],
            [], schema);

        var result = await engine.Explain(new CheckRequest
        {
            EntityType = "resource", EntityId = "v2-expl-skip1",
            Permission = "read",
            SubjectType = "user", SubjectId = "alice"
        }, CancellationToken.None);

        result.Result.Should().BeTrue();
        var allNodes = CollectAllNodes(result.Root);
        allNodes.Should().Contain(n => n.Detail == "skipped (evaluation stopped after a success)");
    }
}
