using System.Diagnostics.Metrics;
using FluentAssertions;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.InMemory.Tests.V2;

public class V2WaveMetricsSpecs
{
    // Counters/histograms are process-global statics and other tests run in parallel, so
    // assertions here check for a specific recorded value or a ">=" delta, never an exact count.
    private static async Task<(List<long> WaveOps, long SameKindOps, bool Result)> MeasureCheck(
        string schemaText, RelationTuple[] tuples, AttributeTuple[]? attributes, string permission)
    {
        var waveOps = new List<long>();
        long sameKindOps = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Valtuutus" &&
                inst.Name is "valtuutus.check.wave_ops" or "valtuutus.check.wave_same_kind_ops")
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
        {
            if (inst.Name == "valtuutus.check.wave_ops") lock (waveOps) waveOps.Add(value);
            else Interlocked.Add(ref sameKindOps, value);
        });
        listener.Start();

        var result = await CheckPlanExecutorSpecs.RunCheck(schemaText, tuples, attributes, "doc", "1", permission);
        return (waveOps, Interlocked.Read(ref sameKindOps), result);
    }

    private const string DifferentKindSchema = """
        entity user {}
        entity doc {
            relation owner @user;
            attribute published bool;
            permission view := owner or published;
        }
        """;

    [Fact]
    public async Task Wave_of_different_kind_ops_records_the_wave_size()
    {
        // owner (HasDirectRelation) and published (HasTrueBoolAttribute) become ready in the
        // same DrainReady pass -> one wave of size 2, but they don't share an OpKind.
        var (waveOps, _, result) = await MeasureCheck(DifferentKindSchema,
            [new RelationTuple("doc", "1", "owner", "user", "u1")], [], "view");

        result.Should().BeTrue();
        waveOps.Should().Contain(2, "owner and published become ready in the same wave");
    }

    private const string SameKindSchema = """
        entity user {}
        entity doc {
            attribute a0 bool;
            attribute a1 bool;
            permission p0 := a0;
            permission p1 := a1;
            permission view := p0 or p1;
        }
        """;

    [Fact]
    public async Task Wave_of_same_kind_ops_credits_both_as_coalescable()
    {
        // a0 and a1 each sit behind their own one-line permission alias (p0/p1) instead of being
        // direct Union siblings: even where sibling fusion exists (RelationalPlanRewriter in
        // Valtuutus.Data.Db; the InMemory path applies no rewriters at all), it only recognizes
        // ≥2 sibling same-entity attribute refs among one Union's direct children, and view's
        // Union children here are PlanRefNode(p0)/PlanRefNode(p1) — Permission type, not
        // Attribute. p0/p1 each resolve through their own separate one-node plan straight to
        // a0/a1's AttributeTruthNode, so both still submit as individual HasTrueBoolAttribute
        // ops ready in the same wave -> both count toward WaveSameKindOps (what a same-shape
        // coalescer could merge).
        var (waveOps, sameKindOps, result) = await MeasureCheck(SameKindSchema,
            [], [new AttributeTuple("doc", "1", "a1", System.Text.Json.Nodes.JsonValue.Create(true))], "view");

        result.Should().BeTrue();
        waveOps.Should().Contain(2);
        sameKindOps.Should().BeGreaterThanOrEqualTo(2,
            "both a0 and a1 share OpKind.HasTrueBoolAttribute within the same wave");
    }
}
