using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class MetricsSpecs
{
    private const string Schema = """
        entity user {}
        entity doc {
            relation owner @user;
            attribute public bool;
            permission edit := owner;
        }
        """;

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddValtuutusCore(Schema).AddInMemory();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_check_records_its_duration_and_round_trips_once()
    {
        await using var sp = Build();
        await sp.GetRequiredService<IDataWriterProvider>()
            .Write([new RelationTuple("doc", "d1", "owner", "user", "alice")], [], default);
        var engine = sp.GetRequiredService<ICheckEngine>();

        using var metrics = new MeasurementCollector();
        await engine.Check(new CheckRequest("doc", "d1", "edit", "user", "alice"), default);

        var duration = metrics.Of("valtuutus.operation.duration").Should().ContainSingle().Subject;
        duration.Value.Should().BePositive();
        duration.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            ["valtuutus.operation"] = "check",
            ["valtuutus.snap_token.source"] = "latest",
            ["valtuutus.result"] = true,
        });
        // InMemory resolves the latest token in a round trip of its own, then checks the relation.
        metrics.Of("valtuutus.operation.round_trips").Should().ContainSingle().Which.Value.Should().Be(2);
        metrics.Of("valtuutus.cache.requests").Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_call_records_its_error_type()
    {
        await using var sp = Build();
        var engine = sp.GetRequiredService<ILookupEntityEngine>();

        using var metrics = new MeasurementCollector();
        var act = () => engine.LookupEntity(new LookupEntityRequest("doc", "edit", "user", "alice")
        {
            Scope = new EntityScope("missing", "user", "alice")
        }, default).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();

        var duration = metrics.Of("valtuutus.operation.duration").Should().ContainSingle().Subject;
        duration.Tags["valtuutus.operation"].Should().Be("lookup_entity");
        duration.Tags["error.type"].Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task Writes_record_their_duration_and_the_tuples_written()
    {
        await using var sp = Build();
        var writer = sp.GetRequiredService<IDataWriterProvider>();

        using var metrics = new MeasurementCollector();
        await writer.Write(
            [new RelationTuple("doc", "d1", "owner", "user", "alice"), new RelationTuple("doc", "d2", "owner", "user", "alice")],
            [new AttributeTuple("doc", "d1", "public", System.Text.Json.Nodes.JsonValue.Create(true))],
            default);
        await writer.Delete(new DeleteFilter
        {
            Relations = [new DeleteRelationsFilter { EntityType = "doc", EntityId = "d2" }]
        }, default);

        metrics.Of("valtuutus.write.duration").Select(m => m.Tags["valtuutus.operation"])
            .Should().Equal("write", "delete");
        metrics.Of("valtuutus.write.tuples").Select(m => (m.Tags["valtuutus.tuple.kind"], m.Value))
            .Should().BeEquivalentTo(new (object?, double)[] { ("relation", 2), ("attribute", 1) });
    }
}
