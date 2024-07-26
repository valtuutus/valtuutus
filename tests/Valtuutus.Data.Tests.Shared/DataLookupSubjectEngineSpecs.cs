using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;
using Valtuutus.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Valtuutus.Data.Tests.Shared;

public abstract class DataLookupSubjectEngineSpecs : BaseLookupSubjectEngineSpecs, IAsyncLifetime
{
    protected abstract void AddSpecificProvider(IValtuutusDataBuilder builder);
    
    protected IDatabaseFixture _fixture = null!;
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var builder = new ServiceCollection()
            .AddSingleton(Substitute.For<ILogger<LookupSubjectEngine>>())
            .AddValtuutusCore(TestsConsts.Action)
            .AddValtuutusData();
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
    
    
    protected sealed override async ValueTask<LookupSubjectEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var lookupSubjectEngine = scope.ServiceProvider.GetRequiredService<LookupSubjectEngine>();
        if(tuples.Length == 0 && attributes.Length == 0) return lookupSubjectEngine;
        var dataEngine = scope.ServiceProvider.GetRequiredService<DataEngine>();
        await dataEngine.Write(tuples, attributes, default);
        return lookupSubjectEngine;
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