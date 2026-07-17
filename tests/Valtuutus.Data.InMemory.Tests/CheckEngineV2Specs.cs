using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class CheckEngineV2Specs : BaseCheckEngineSpecs
{
    public CheckEngineV2Specs(InMemoryFixture fixture) : base(fixture) { }
    protected override bool UseCheckV2 => true;
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddInMemory();
}
