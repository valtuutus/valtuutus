using Microsoft.Data.SqlClient;

namespace Valtuutus.Tests.Containers;

public static class SharedSqlServer
{
    private const int DatabaseInUse = 3702;

    /// <summary>
    /// Drops a database on the shared SQL Server. Switching a database to single-user mode costs
    /// seconds even when nobody is connected, so only a drop refused for open sessions pays for it.
    /// </summary>
    public static async Task DropDatabaseAsync(string serverConnectionString, string database)
    {
        try
        {
            await ExecuteAsync(serverConnectionString, $"DROP DATABASE IF EXISTS [{database}]");
        }
        catch (SqlException ex) when (ex.Number == DatabaseInUse)
        {
            await ExecuteAsync(serverConnectionString,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    private static async Task ExecuteAsync(string serverConnectionString, string sql)
    {
        await using var connection = new SqlConnection(serverConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
