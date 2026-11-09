using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class DeleteFilterSpecs(InMemoryFixture fixture) : BaseDeleteFilterSpecs(fixture)
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddInMemory();
}
