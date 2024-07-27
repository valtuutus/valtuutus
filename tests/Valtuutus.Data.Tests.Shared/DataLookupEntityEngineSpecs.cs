using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;
using Valtuutus.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Valtuutus.Data.Tests.Shared;

public abstract class DataLookupEntityEngineSpecs : BaseLookupEntityEngineSpecs, IAsyncLifetime
{
    
    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);
    
    protected IDatabaseFixture _fixture = null!;
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(TestsConsts.Action);
        AddSpecificProvider(services)
            .AddConcurrentQueryLimit(5);
        
        if (schema != null)
        {
            var serviceDescriptor = services.First(descriptor => descriptor.ServiceType == typeof(Schema));
            services.Remove(serviceDescriptor);
            services.AddSingleton(schema);
        }

        return services.BuildServiceProvider();
    }
    
    protected sealed override async ValueTask<LookupEntityEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var lookupEntityEngine = scope.ServiceProvider.GetRequiredService<LookupEntityEngine>();
        if(tuples.Length == 0 && attributes.Length == 0) return lookupEntityEngine;
        var dataEngine = scope.ServiceProvider.GetRequiredService<DataEngine>();
        await dataEngine.Write(tuples, attributes, default);
        return lookupEntityEngine;
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