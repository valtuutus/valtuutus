using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class LookupEntityEngineSpecs : BaseLookupEntityEngineSpecs
{
    public LookupEntityEngineSpecs(InMemoryFixture fixture) : base(fixture){}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddInMemory();
    }
}