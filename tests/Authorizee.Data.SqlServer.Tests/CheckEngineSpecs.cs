using Authorizee.Core;
using Authorizee.Core.Configuration;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Authorizee.Data.Configuration;
using Authorizee.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Data.SqlServer.Tests;

[Collection("SqlServerSpec")]
public sealed class CheckEngineSpecs : BaseCheckEngineSpecs, IAsyncLifetime
{
    private readonly SqlServerFixture _fixture;

    public CheckEngineSpecs(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var serviceCollection = new ServiceCollection()
            .AddSingleton(Substitute.For<ILogger<SqlServerDataReaderProvider>>())
            .AddSingleton(Substitute.For<ILogger<CheckEngine>>())
            .AddDatabaseSetup(_fixture.DbFactory, o => o.AddSqlServer())
            .AddSchemaConfiguration(TestsConsts.Action);
        if (schema != null)
        {
            var serviceDescriptor = serviceCollection.First(descriptor => descriptor.ServiceType == typeof(Schema));
            serviceCollection.Remove(serviceDescriptor);
            serviceCollection.AddSingleton(schema);
        }

        return serviceCollection.BuildServiceProvider();
    }
    
    protected override async ValueTask<CheckEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var checkEngine = scope.ServiceProvider.GetRequiredService<CheckEngine>();
        var writerProvider = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await writerProvider.Write(tuples, attributes);
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