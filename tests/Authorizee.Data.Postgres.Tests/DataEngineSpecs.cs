using Authorizee.Core;
using Authorizee.Core.Configuration;
using Authorizee.Core.Data;
using Authorizee.Core.Schemas;
using Authorizee.Data.Configuration;
using Authorizee.Tests.Shared;
using Dapper;
using FluentAssertions;
using IdGen;
using IdGen.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Sqids;

namespace Authorizee.Data.Postgres.Tests;


[Collection("PostgreSqlSpec")]
public class DataEngineSpecs : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public DataEngineSpecs(PostgresFixture fixture)
    {
        _fixture = fixture;
    }
    
    private ServiceProvider CreateServiceProvider()
    {
        var serviceCollection = new ServiceCollection()
            .AddSingleton(Substitute.For<ILogger<IDataReaderProvider>>())
            .AddDatabaseSetup(_fixture.DbFactory, o => o.AddPostgres())
            .AddSchemaConfiguration(TestsConsts.Action);

        serviceCollection.Remove(serviceCollection.First(descriptor => descriptor.ServiceType == typeof(IIdGenerator<long>)));
        serviceCollection.AddIdGen(0, () => new IdGeneratorOptions
        {
            TimeSource = new MockAutoIncrementingIntervalTimeSource(1)
        });

        return serviceCollection.BuildServiceProvider();
    }
    
    [Fact]
    public async Task WritingData_ShouldAssociateRelationWithTransactionId()
    {
        // arrange
        var provider = CreateServiceProvider();
        
        // act
        var dataEngine = provider.GetRequiredService<DataEngine>();
        var snapToken = await dataEngine.Write([new RelationTuple("project", "1", "member", "user", "1")], [], default);
        var decoder = provider.GetRequiredService<SqidsEncoder<long>>();
        var transactionId = decoder.Decode(snapToken.Value).Single();

        // assert
        using var db = _fixture.DbFactory();
        var relationCount = await db.ExecuteScalarAsync<bool>("SELECT (SELECT COUNT(*) FROM public.relation_tuples WHERE created_tx_id = @id) = 1", 
            new { id = transactionId });
        
        relationCount.Should().BeTrue();
        
        var exists = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.transactions WHERE id = @id)", 
            new { id = transactionId });
        
        exists.Should().BeTrue();
    }
    
    [Fact]
    public async Task DeletingData_ShouldReturnTransactionId()
    {
        // arrange
        var provider = CreateServiceProvider();
        var dataEngine = provider.GetRequiredService<DataEngine>();
        
        // act
        var decoder = provider.GetRequiredService<SqidsEncoder<long>>();
        var newSnapToken = await dataEngine.Delete(new DeleteFilter
        {
            Relations = new[] { new DeleteRelationsFilter
            {
                EntityType = "project",
                EntityId = "1",
                Relation = "member",
                SubjectType = "user",
                SubjectId = "1"
            
            } }
        }, default);
        
        
        // assert
        using var db = _fixture.DbFactory();
        
        var newTransactionId = decoder.Decode(newSnapToken.Value).Single();
        // new transaction should exist
        var newTransaction = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM public.transactions WHERE id = @id)", 
            new { id = newTransactionId });
        
        newTransaction.Should().BeTrue();
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