using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class MixedRelationTargetsSpecs(InMemoryFixture fixture)
    : BaseMixedRelationTargetsSpecs(fixture), IClassFixture<InMemoryFixture>
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services) => services.AddInMemory();
}
