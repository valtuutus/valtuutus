using Dapper;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Valtuutus.Core.Configuration;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Data.Db;

namespace Valtuutus.Benchmarks;

public static class CommonSetup
{
    public static async Task<(ServiceProvider serviceProvider, ICheckEngine checkEngine)> MigrateAndSeed(
        DbConnectionFactory dbFactory, Action<IServiceCollection> configureProvider, Assembly migrationAssembly)
    {

        var schemaFilePath = Assembly.GetExecutingAssembly()
            .GetManifestResourceNames()
            .First(c => c.EndsWith("schema.vtt"));
        var schema = Assembly.GetExecutingAssembly().GetManifestResourceStream(schemaFilePath)!;
        
        var serviceCollection = new ServiceCollection()
            .AddValtuutusCore(schema);
        
        using var connection = dbFactory();
        
        configureProvider(serviceCollection);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var checkEngine = serviceProvider.GetRequiredService<ICheckEngine>();
        
        
        var migrations = migrationAssembly
            .GetManifestResourceNames()
            .Where(x => x.EndsWith(".sql"))
            .OrderBy(x => x)
            .ToList();

        foreach (var migrationFile in migrations!)
        {
            await using var stream = migrationAssembly.GetManifestResourceStream(migrationFile);
            using var reader = new StreamReader(stream!);
            var migrationSql = await reader.ReadToEndAsync();
            await connection.ExecuteAsync(migrationSql);
        }

        var (relations, attributes) = Seeder.Seeder.GenerateData();
        var writerProvider = serviceProvider.GetRequiredService<IDataWriterProvider>();
        await writerProvider.Write(relations, attributes, default);
        
        return (serviceProvider, checkEngine);
    }
}