using Valtuutus.Data.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;


[CollectionDefinition("InMemorySpecs")]
public sealed class InMemorySpecsFixture : ICollectionFixture<InMemoryFixture>
{
}


public class InMemoryFixture :  IDatabaseFixture
{
    public DbConnectionFactory DbFactory { get; private set; } = default!;
    
    public Task ResetDatabaseAsync()
    {
        return Task.CompletedTask;
    }
    
}