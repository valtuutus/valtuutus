using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;
using Valtuutus.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Valtuutus.Data.Tests.Shared;

public abstract class DataCheckEngineSpecs : BaseCheckEngineSpecs, IAsyncLifetime
{
    protected IDatabaseFixture _fixture = null!;

    protected abstract void AddSpecificProvider(IValtuutusDataBuilder builder);
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var builder = new ServiceCollection()
            .AddSingleton(Substitute.For<ILogger<IDataReaderProvider>>())
            .AddSingleton(Substitute.For<ILogger<CheckEngine>>())
            .AddValtuutusCore(TestsConsts.Action)
            .AddValtuutusData()
            .AddConcurrentQueryLimit(3);
        
        AddSpecificProvider(builder);
        
        var serviceCollection = builder.Services;
        if (schema != null)
        {
            var serviceDescriptor = serviceCollection.First(descriptor => descriptor.ServiceType == typeof(Schema));
            serviceCollection.Remove(serviceDescriptor);
            serviceCollection.AddSingleton(schema);
        }

        return serviceCollection.BuildServiceProvider();
    }
    
    protected sealed override async ValueTask<CheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var checkEngine = scope.ServiceProvider.GetRequiredService<CheckEngine>();
        if(tuples.Length == 0 && attributes.Length == 0) return checkEngine;
        var dataEngine = scope.ServiceProvider.GetRequiredService<DataEngine>();
        await dataEngine.Write(tuples, attributes, default);
        return checkEngine;
    }
    
    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fixture.ResetDatabaseAsync();
    }
}