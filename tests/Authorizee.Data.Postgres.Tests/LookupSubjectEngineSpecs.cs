using System.Collections.Concurrent;
using Authorizee.Core;
using Authorizee.Core.Configuration;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Authorizee.Data.Configuration;
using Authorizee.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Authorizee.Data.Postgres.Tests;

[Collection("PostgreSqlSpec")]
public sealed class LookupSubjectEngineSpecs : BaseLookupSubjectEngineSpecs, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public LookupSubjectEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }
    
    private ServiceProvider CreateServiceProvider(Schema? schema = null)
    {
        var serviceCollection = new ServiceCollection()
            .AddSingleton(Substitute.For<ILogger<PostgresDataReaderProvider>>())
            .AddSingleton(Substitute.For<ILogger<LookupSubjectEngine>>())
            .AddDatabaseSetup(_fixture.DbFactory, o => o.AddPostgres())
            .AddSchemaConfiguration(TestsConsts.Action);
        if (schema != null)
        {
            var serviceDescriptor = serviceCollection.First(descriptor => descriptor.ServiceType == typeof(Schema));
            serviceCollection.Remove(serviceDescriptor);
            serviceCollection.AddSingleton(schema);
        }

        return serviceCollection.BuildServiceProvider();
    }
    
    
    protected override async ValueTask<LookupSubjectEngine> CreateEngine(RelationTuple[] tuples, AttributeTuple[] attributes, Schema? schema = null)
    {
        var serviceProvider = CreateServiceProvider(schema);
        var scope = serviceProvider.CreateScope();
        var lookupSubjectEngine = scope.ServiceProvider.GetRequiredService<LookupSubjectEngine>();
        var writerProvider = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await writerProvider.Write(tuples, attributes);
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