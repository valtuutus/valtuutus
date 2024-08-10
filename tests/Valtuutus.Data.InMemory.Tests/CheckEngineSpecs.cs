using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class CheckEngineSpecs : BaseCheckEngineSpecs
{
    public CheckEngineSpecs(InMemoryFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddInMemory();
    }
}