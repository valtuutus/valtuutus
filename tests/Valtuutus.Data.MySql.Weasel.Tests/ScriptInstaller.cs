using MySqlConnector;

namespace Valtuutus.Data.MySql.Weasel.Tests;

/// <summary>Installs the provider's migration scripts under the given database and table names.</summary>
public static class ScriptInstaller
{
    public static IReadOnlyList<string> ScriptNames() =>
        typeof(ValtuutusMySqlOptions).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    public static async Task InstallAsync(MySqlConnection connection, ValtuutusMySqlOptions options, string database, int? onlyFirst = null)
    {
        foreach (var name in ScriptNames().Take(onlyFirst ?? int.MaxValue))
        {
            var sql = ReadScript(name)
                .Replace("CREATE TABLE transactions", $"CREATE TABLE `{database}`.`{options.TransactionsTableName}`")
                .Replace("CREATE TABLE relation_tuples", $"CREATE TABLE `{database}`.`{options.RelationsTableName}`")
                .Replace("CREATE TABLE attributes", $"CREATE TABLE `{database}`.`{options.AttributesTableName}`");
            await Exec(connection, sql);
        }
    }

    private static string ReadScript(string name)
    {
        using var stream = typeof(ValtuutusMySqlOptions).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static async Task Exec(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
