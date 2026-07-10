using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using System.Data;
using Testcontainers.MsSql;
using Valtuutus.Data.SqlServer;

namespace Valtuutus.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[JsonExporterAttribute.FullCompressed]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class SqlServerBenchmarks : BenchmarkBase
{
    private readonly MsSqlContainer _msSqlContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-CU13-ubuntu-22.04")
        .WithPassword("Valtuutus123!")
        .WithName($"mssql-benchmarks-{Guid.NewGuid()}")
        .Build();

    private ServiceProvider _serviceProvider = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        await _msSqlContainer.StartAsync();
        IDbConnection DbFactory() => new SqlConnection(_msSqlContainer.GetConnectionString());
        var mssqlAssembly = typeof(ValtuutusSqlServerOptions).Assembly;
        (_serviceProvider, _checkEngine, _lookupEntityEngine, _lookupSubjectEngine) = await CommonSetup.MigrateAndSeed(
            sc => sc.AddSqlServer(_ => DbFactory),
            Seeder.Seeder.GenerateData(),
            mssqlAssembly);

        // Mirrors the Postgres ANALYZE call: AUTO_CREATE_STATISTICS/AUTO_UPDATE_STATISTICS are
        // on by default and normally self-heal synchronously on first compile, but pin it
        // explicitly so benchmark numbers reflect steady-state plans rather than whatever the
        // very first query's auto-created (possibly low-sample) stats produced.
        await using (var connection = new SqlConnection(_msSqlContainer.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE STATISTICS relation_tuples WITH FULLSCAN; UPDATE STATISTICS attributes WITH FULLSCAN;";
            await command.ExecuteNonQueryAsync();
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _msSqlContainer.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }
}
