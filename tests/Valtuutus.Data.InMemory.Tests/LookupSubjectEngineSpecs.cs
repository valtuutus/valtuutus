using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class LookupSubjectEngineSpecs : BaseLookupSubjectEngineSpecs, IClassFixture<InMemoryFixture>
{

    public LookupSubjectEngineSpecs(InMemoryFixture fixture) : base(fixture) {}

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
        return services.AddInMemory();
    }
}