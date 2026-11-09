using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

public class SnapTokenSpecs : BaseSnapTokenSpecs, IClassFixture<InMemoryFixture>
{
    public SnapTokenSpecs(InMemoryFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddInMemory();
    }
}