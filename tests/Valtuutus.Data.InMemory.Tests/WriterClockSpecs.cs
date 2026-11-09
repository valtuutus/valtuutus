using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

public sealed class WriterClockSpecs : BaseWriterClockSpecs, IClassFixture<InMemoryFixture>
{
    public WriterClockSpecs(InMemoryFixture fixture) : base(fixture)
    {
    }

    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
        => services.AddInMemory();
}
