using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public class SnapTokenSpecs : BaseSnapTokenSpecs
{
    public SnapTokenSpecs(InMemoryFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddInMemory();
    }
}