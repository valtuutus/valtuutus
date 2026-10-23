using System.Collections.Concurrent;
using MySqlConnector;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.MySql;

internal static class MySqlConnections
{
    private static readonly ConcurrentDictionary<string, MySqlDataSource> DataSourceCache = new();

    // One pool per connection string, shared by every reader built from the same factory.
    internal static MySqlDataSource GetOrCreateDataSource(string connectionString) =>
        DataSourceCache.GetOrAdd(connectionString, static cs => new MySqlDataSource(cs));

    internal static string DbSystem(MySqlServerType serverType) =>
        serverType == MySqlServerType.MariaDb ? "mariadb" : "mysql";

    internal static string Table(string schema, string table) =>
        schema.Length == 0 ? $"`{table}`" : $"`{schema}`.`{table}`";

    internal static string Relations(IValtuutusDbOptions o) => Table(o.Schema, o.RelationsTableName);
    internal static string Attributes(IValtuutusDbOptions o) => Table(o.Schema, o.AttributesTableName);
    internal static string Transactions(IValtuutusDbOptions o) => Table(o.Schema, o.TransactionsTableName);
}
