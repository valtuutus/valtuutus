using System.Text.RegularExpressions;
using Npgsql;

namespace Valtuutus.Data.Postgres.Weasel.Tests;

/// <summary>Installs the provider's migration scripts under the given schema and table names.</summary>
public static class ScriptInstaller
{
    public static IReadOnlyList<string> ScriptNames() =>
        typeof(ValtuutusPostgresOptions).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    public static async Task InstallAsync(NpgsqlConnection connection, ValtuutusPostgresOptions options, int? onlyFirst = null)
    {
        await Exec(connection, $"CREATE SCHEMA IF NOT EXISTS \"{options.Schema}\"");
        foreach (var name in ScriptNames().Take(onlyFirst ?? int.MaxValue))
        {
            await Exec(connection, Rename(ReadScript(name), options));
        }
    }

    private static string ReadScript(string name)
    {
        using var stream = typeof(ValtuutusPostgresOptions).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Rename(string sql, ValtuutusPostgresOptions o)
    {
        sql = Regex.Replace(sql, @"""public""\.|\bpublic\.", $"\"{o.Schema}\".");
        foreach (var (from, to) in new[] { ("relation_tuples", o.RelationsTableName), ("attributes", o.AttributesTableName), ("transactions", o.TransactionsTableName) })
            sql = Regex.Replace(sql, $@"(?<=\""{Regex.Escape(o.Schema)}\""\.)(""{from}""|{from}\b)", $"\"{to}\"");
        return sql;
    }

    public static async Task Exec(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
