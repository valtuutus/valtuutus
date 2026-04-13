using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class CheckEngineSpecs : BaseCheckEngineSpecs
{
    public CheckEngineSpecs(InMemoryFixture fixture) : base(fixture){}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddInMemory();
    }

    [Fact]
    public async Task Check_OnEmptyDatabase_ReturnsFalse()
    {
        // Fresh InMemory store with no writes — GetLatestSnapToken returns null,
        // SnapTokenUtils should resolve to SnapToken.MinValue without throwing.
        var services = new ServiceCollection();
        services.AddValtuutusCore("""
            entity user {}
            entity doc {
                relation owner @user;
                permission view := owner;
            }
            """)
            .AddInMemory();
        await using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<ICheckEngine>();

        var result = await engine.Check(new CheckRequest
        {
            EntityType = "doc",
            EntityId = "1",
            Permission = "view",
            SubjectType = "user",
            SubjectId = "u1"
        }, CancellationToken.None);

        Assert.False(result);
    }
}