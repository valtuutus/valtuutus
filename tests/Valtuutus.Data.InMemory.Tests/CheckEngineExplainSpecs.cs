using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core.Configuration;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class CheckEngineExplainSpecs : BaseCheckEngineExplainSpecs
{
    public CheckEngineExplainSpecs(InMemoryFixture fixture) : base(fixture) { }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddInMemory();
}
