using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class DeleteFilterSpecs(InMemoryFixture fixture) : BaseDeleteFilterSpecs(fixture), IClassFixture<InMemoryFixture>
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) =>
        services.AddInMemory();
}
