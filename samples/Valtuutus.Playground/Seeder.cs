using Dapper;
using Microsoft.Data.SqlClient;
using Npgsql;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;

namespace Valtuutus.Playground;


public static class Seeder {
    public static async Task SeedPostgres(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();

        await using var connection = (NpgsqlConnection)factory();
        await connection.OpenAsync();

        var relationTuples = await connection.ExecuteScalarAsync<int>("select count(*) from public.relation_tuples");

        var attrs = await connection.ExecuteScalarAsync<int>("select count(*) from public.attributes");

        if (relationTuples > 0 && attrs > 0)
        {
            return;
        }

        Console.WriteLine("Generating data");
        var (relations, attributes) = Valtuutus.Seeder.Seeder.GenerateData();

        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        
        Console.WriteLine("Writing data to database...");
        await writer.Write(relations, attributes, default);
        Console.WriteLine("Done.");

    }

    public static async Task SeedSqlServer(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();

        await using var connection = (SqlConnection)factory();
        await connection.OpenAsync();

        var relationTuples = await connection.ExecuteScalarAsync<int>("select count(*) from dbo.relation_tuples");

        var attrs = await connection.ExecuteScalarAsync<int>("select count(*) from dbo.attributes");

        if (relationTuples > 0 && attrs > 0)
        {
            return;
        }

        var (relations, attributes) = Valtuutus.Seeder.Seeder.GenerateData();
        
        var writer = scope.ServiceProvider.GetRequiredService<IDataWriterProvider>();
        await writer.Write(relations, attributes, default);
    }

}