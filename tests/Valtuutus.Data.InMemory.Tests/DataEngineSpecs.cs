using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Data.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;


[Collection("InMemorySpecs")]
public sealed class DataEngineSpecs : DataSpecificDataEngineSpecs
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
       return services.AddInMemory();
    }

    protected override Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples()
    {
        var controller = _provider.GetRequiredService<InMemoryController>();
        return controller.Dump(default);
    }

    public DataEngineSpecs(InMemoryFixture fixture)
    {
        _fixture = fixture;
    }
    
    
}