using Valtuutus.Data.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;

[Collection("InMemorySpecs")]
public sealed class CheckEngineSpecs : DataCheckEngineSpecs
{
    public CheckEngineSpecs(InMemoryFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void AddSpecificProvider(IValtuutusDataBuilder builder)
    {
        builder.AddInMemory();
    }
}