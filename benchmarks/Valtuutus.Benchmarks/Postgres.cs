using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Reflection;
using Testcontainers.PostgreSql;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;
using Valtuutus.Data.Postgres;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class Postgres
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder()
        .WithUsername("Valtuutus")
        .WithPassword("Valtuutus123")
        .WithDatabase("Valtuutus")
        .WithName($"pg-benchmarks-{Guid.NewGuid()}")
        .Build();

    private ServiceProvider _serviceProvider = null!;
    private ICheckEngine _checkEngine = null!;
    
    [ParamsSource(nameof(ValuesForCheckRequest))]
    public CheckRequest CheckRequestParam { get; set; } = null!;

    public IEnumerable<CheckRequest> ValuesForCheckRequest =>
        // simple relation check
        new CheckRequest[] { new ()
        {
            Permission = "admin",
            EntityType = "organization",
            EntityId = "5171869f-b4e4-ca9a-b800-5e1dab069a26",
            SubjectType = "user",
            SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
            
        },
            // complex permissions, with function evaluation from attribute
            new ()
            {
                Permission = "edit",
                EntityType = "project",
                EntityId = "e4010d7b-cea1-94c6-2232-e1f9ae557272",
                SubjectType = "user",
                SubjectId = "3fca4119-3bda-4370-13cd-a3d317459c73"
            }
            
        };

    [GlobalSetup]
    public async Task Setup()
    {
        var starting = _dbContainer.StartAsync();

        var schemaFilePath = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .First(c => c.EndsWith("schema.vtt"));
        var schema = Assembly.GetExecutingAssembly().GetManifestResourceStream(schemaFilePath)!;
        
        var serviceCollection = new ServiceCollection()
            .AddValtuutusCore(schema);

        await starting;
        
        DbConnectionFactory dbFactory = () => new NpgsqlConnection(_dbContainer.GetConnectionString());

        using var connection = dbFactory();
        serviceCollection.AddPostgres(_ => dbFactory);
        
        _serviceProvider = serviceCollection.BuildServiceProvider();
        _checkEngine = _serviceProvider.GetRequiredService<ICheckEngine>();
        
        var assembly = typeof(ValtuutusPostgresOptions).Assembly;
        
        var migrations = assembly
            .GetManifestResourceNames()
            .Where(x => x.EndsWith(".sql"))
            .OrderBy(x => x)
            .ToList();

        foreach (var migrationFile in migrations!)
        {
            await using var stream = assembly.GetManifestResourceStream(migrationFile);
            using var reader = new StreamReader(stream!);
            var migrationSql = await reader.ReadToEndAsync();
            await connection.ExecuteAsync(migrationSql);
        }

        var (relations, attributes) = Seeder.Seeder.GenerateData();
        var writerProvider = _serviceProvider.GetRequiredService<IDataWriterProvider>();
        await writerProvider.Write(relations, attributes, default);
    }

    [Benchmark]
    public async Task<bool> CheckRequest()
    {
        return await _checkEngine.Check(CheckRequestParam, default);
    }
    
    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _dbContainer.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }
    
}