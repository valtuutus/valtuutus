using System.Diagnostics.Metrics;
using FluentAssertions;
using Valtuutus.Core;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.InMemory.Tests.V2;

public class V2MetricsSpecs
{
    // Counters are process-global statics and other tests run in parallel, so every assertion
    // here is a ">= delta" over a window, never an exact count or a zero-check.
    private static async Task<(long ShortCircuits, long FirstChildDecided, bool Result)> MeasureCheck(
        RelationTuple[] tuples, AttributeTuple[]? attributes, string permission)
    {
        long shortCircuits = 0, firstChild = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Valtuutus" &&
                inst.Name is "valtuutus.check.short_circuits" or "valtuutus.check.first_child_decided")
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, value, _, _) =>
        {
            if (inst.Name == "valtuutus.check.short_circuits") Interlocked.Add(ref shortCircuits, value);
            else Interlocked.Add(ref firstChild, value);
        });
        listener.Start();

        // `published or owner`: one attribute + one relation, so the union always keeps its
        // expression frame (which these counters hang off) — no sibling pair a rewriter could
        // ever fuse away, and the InMemory path applies no rewriters anyway.
        const string s = """
            entity user {}
            entity doc {
                relation owner @user;
                attribute published bool;
                permission view := published or owner;
                permission both := published and owner;
            }
            """;
        var result = await CheckPlanExecutorSpecs.RunCheck(s, tuples, attributes, "doc", "1", permission);
        return (Interlocked.Read(ref shortCircuits), Interlocked.Read(ref firstChild), result);
    }

    [Fact]
    public async Task Short_circuit_with_a_live_sibling_increments_ShortCircuits()
    {
        // Both children would be true; whichever lands first decides while the other is
        // still unresolved -> ShortCircuits must move.
        var (sc, _, result) = await MeasureCheck(
            [new RelationTuple("doc", "1", "owner", "user", "u1")],
            [new AttributeTuple("doc", "1", "published", System.Text.Json.Nodes.JsonValue.Create(true))],
            "view");
        result.Should().BeTrue();
        sc.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Union_decided_by_child_zero_increments_FirstChildDecided()
    {
        // published (child 0) is true, owner is false: child 0's completion decides the union.
        var (_, fcd, result) = await MeasureCheck(
            [],
            [new AttributeTuple("doc", "1", "published", System.Text.Json.Nodes.JsonValue.Create(true))],
            "view");
        result.Should().BeTrue();
        fcd.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Intersect_decided_false_by_child_zero_increments_FirstChildDecided()
    {
        // published (child 0) is absent -> false decides the intersect; owner true is irrelevant.
        var (_, fcd, result) = await MeasureCheck(
            [new RelationTuple("doc", "1", "owner", "user", "u1")],
            [],
            "both");
        result.Should().BeFalse();
        fcd.Should().BeGreaterThanOrEqualTo(1);
    }
}
