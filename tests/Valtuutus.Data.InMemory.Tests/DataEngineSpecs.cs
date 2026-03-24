using Microsoft.Extensions.DependencyInjection;
using Valtuutus.Core;
using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;


[Collection("InMemorySpecs")]
public sealed class DataEngineSpecs : BaseDataEngineSpecs
{
    protected override IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services)
    {
       return services.AddInMemory();
    }

    protected override Task<(RelationTuple[] relations, AttributeTuple[] attributes)> GetCurrentTuples()
    {
        var relations = Provider.GetRequiredService<RelationsStore>();
        var attributes = Provider.GetRequiredService<AttributesStore>();
        return Task.FromResult((relations.Dump(), attributes.Dump()));
    }

    public DataEngineSpecs(InMemoryFixture fixture) : base(fixture) {}
    
    
}