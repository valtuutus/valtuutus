using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Valtuutus.Data.SqlServer.Weasel.Tests;

/// <summary>Installs the provider's migration scripts under the given schema and table names.</summary>
public static class ScriptInstaller
{
    public static IReadOnlyList<string> ScriptNames() =>
        typeof(ValtuutusSqlServerOptions).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    public static async Task InstallAsync(SqlConnection connection, ValtuutusSqlServerOptions options, int? onlyFirst = null)
    {
        await Exec(connection, $"IF SCHEMA_ID('{options.Schema}') IS NULL EXEC('CREATE SCHEMA [{options.Schema}]')");
        foreach (var name in ScriptNames().Take(onlyFirst ?? int.MaxValue))
        {
            await Exec(connection, Rename(ReadScript(name), options));
        }
    }

    private static string ReadScript(string name)
    {
        using var stream = typeof(ValtuutusSqlServerOptions).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Rename(string sql, ValtuutusSqlServerOptions o)
    {
        foreach (var (from, to) in new[] { ("attributes", o.AttributesTableName), ("relation_tuples", o.RelationsTableName), ("transactions_pending", $"{o.TransactionsTableName}_pending"), ("transactions", o.TransactionsTableName) })
        {
            var target = $"[{o.Schema}].[{to}]";
            sql = Regex.Replace(sql, $@"\[dbo\]\.\[{from}\]", target);
            sql = Regex.Replace(sql, $@"TABLE \[{from}\]", $"TABLE {target}");
            sql = Regex.Replace(sql, $@" ON {from} ", $" ON {target} ");
            sql = sql.Replace($"[PK_{from}]", $"[PK_{to}]");
        }
        return sql;
    }

    public static async Task Exec(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
