using Valtuutus.Core;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;
using Valtuutus.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Valtuutus.Core.Engines.Check;

namespace Valtuutus.Data.Tests.Shared;

public abstract class DataCheckEngineSpecs : BaseCheckEngineSpecs, IAsyncLifetime
{
    protected IDatabaseFixture _fixture = null!;

    protected abstract IValtuutusDataBuilder AddSpecificProvider(IServiceCollection services);
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var services = new ServiceCollection()
            .AddValtuutusCore(TestsConsts.Action);
        
        AddSpecificProvider(services)
            .AddConcurrentQueryLimit(3);

        
        if (schema != null)
        {
            var serviceDescriptor = services.First(descriptor => descriptor.ServiceType == typeof(Schema));
            services.Remove(serviceDescriptor);
            services.AddSingleton(schema);
        }

        return services.BuildServiceProvider();
    }
    
    protected sealed override async ValueTask<ICheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var checkEngine = scope.ServiceProvider.GetRequiredService<ICheckEngine>();
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