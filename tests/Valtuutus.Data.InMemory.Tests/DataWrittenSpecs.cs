using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class DataWrittenSpecs : BaseDataWrittenSpecs
{
    public DataWrittenSpecs(InMemoryFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddInMemory();
}
