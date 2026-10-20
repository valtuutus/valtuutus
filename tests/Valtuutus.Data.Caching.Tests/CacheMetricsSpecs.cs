using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.InMemory;
using Valtuutus.Tests.Shared;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Valtuutus.Data.Caching.Tests;

public sealed class CacheMetricsSpecs
{
    private const string Schema = """
        entity user {}
        entity doc {
            relation owner @user;
            permission edit := owner;
        }
        """;

    [Fact]
    public async Task A_cached_call_is_measured_once_whether_the_cache_answers_or_not()
    {
        var services = new ServiceCollection();
        services.AddFusionCache();
        services.AddValtuutusCore(Schema).AddInMemory().AddCaching();
        await using var sp = services.BuildServiceProvider();
        var written = await sp.GetRequiredService<IDataWriterProvider>()
            .Write([new RelationTuple("doc", "d1", "owner", "user", "alice")], [], default);
        var engine = sp.GetRequiredService<ICheckEngine>();
        var request = new CheckRequest("doc", "d1", "edit", "user", "alice", snapToken: written);

        using var metrics = new MeasurementCollector();
        await engine.Check(request, default);
        await engine.Check(request, default);

        metrics.Of("valtuutus.operation.duration")
            .Select(m => (m.Tags["valtuutus.cache.hit"], m.Tags["valtuutus.result"], m.Tags["valtuutus.operation"]))
            .Should().Equal((false, true, "check"), (true, true, "check"));
        metrics.Of("valtuutus.operation.round_trips").Select(m => m.Value).Should().Equal(1, 0);
        metrics.Of("valtuutus.cache.requests").Select(m => m.Tags["valtuutus.cache.hit"]).Should().Equal(false, true);
    }
}
