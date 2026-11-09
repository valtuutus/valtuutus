using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class DataWrittenSpecs : BaseDataWrittenSpecs, IClassFixture<InMemoryFixture>
{
    public DataWrittenSpecs(InMemoryFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddInMemory();
}
