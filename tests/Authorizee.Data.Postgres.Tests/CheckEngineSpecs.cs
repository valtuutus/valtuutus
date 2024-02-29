using Authorizee.Core;
using Authorizee.Core.Configuration;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Authorizee.Data.Configuration;
using Authorizee.Tests.Shared;
using IdGen;
using IdGen.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class CheckEngineSpecs : BaseCheckEngineSpecs, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public CheckEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var serviceCollection = new ServiceCollection()
            .AddSingleton(Substitute.For<ILogger<IDataReaderProvider>>())
            .AddSingleton(Substitute.For<ILogger<CheckEngine>>())
            .AddDatabaseSetup(_fixture.DbFactory, o => o.AddPostgres())
            .AddSchemaConfiguration(TestsConsts.Action);
        if (schema != null)
        {
            var serviceDescriptor = serviceCollection.First(descriptor => descriptor.ServiceType == typeof(Schema));
            serviceCollection.Remove(serviceDescriptor);
            serviceCollection.AddSingleton(schema);
        }
        serviceCollection.Remove(serviceCollection.First(descriptor => descriptor.ServiceType == typeof(IIdGenerator<long>)));
        serviceCollection.AddIdGen(0, () => new IdGeneratorOptions
        {
            TimeSource = new MockAutoIncrementingIntervalTimeSource(1)
        });

        return serviceCollection.BuildServiceProvider();
    }
    
    protected override async ValueTask<CheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
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