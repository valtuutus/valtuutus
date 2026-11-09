using Valtuutus.Tests.Shared;

namespace Valtuutus.Data.InMemory.Tests;



public class InMemoryFixture :  IDatabaseFixture
{
    
    public Task ResetDatabaseAsync()
    {
        return Task.CompletedTask;
    }
    
}