using System.Text.RegularExpressions;
using MySqlConnector;

namespace Valtuutus.Data.MySql.Weasel.Tests;

/// <summary>SHOW CREATE TABLE per table, without the AUTO_INCREMENT counter.</summary>
public static class SchemaSnapshot
{
    public static async Task<IReadOnlyList<string>> TakeAsync(MySqlConnection connection, string database, ValtuutusMySqlOptions o)
    {
        var rows = new List<string>();
        foreach (var table in new[] { o.TransactionsTableName, o.RelationsTableName, o.AttributesTableName })
        {
            await using var c = new MySqlCommand($"SHOW CREATE TABLE `{database}`.`{table}`", connection);
            await using var r = await c.ExecuteReaderAsync();
            await r.ReadAsync();
            rows.Add(Regex.Replace(r.GetString(1), @" AUTO_INCREMENT=\d+", ""));
        }
        return rows;
    }
}
