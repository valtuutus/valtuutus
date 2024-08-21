using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;


[CollectionDefinition("InMemorySpecs")]
public sealed class InMemorySpecsFixture : ICollectionFixture<InMemoryFixture>
{
}


public class InMemoryFixture :  IDatabaseFixture
{
    
    public Task ResetDatabaseAsync()
    {
        return Task.CompletedTask;
    }
    
}