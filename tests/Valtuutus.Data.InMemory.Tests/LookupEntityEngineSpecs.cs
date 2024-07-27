using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Data.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class LookupEntityEngineSpecs : DataLookupEntityEngineSpecs
{
    public LookupEntityEngineSpecs(InMemoryFixture fixture)
    {
        _fixture = fixture;
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddInMemory();
    }
}