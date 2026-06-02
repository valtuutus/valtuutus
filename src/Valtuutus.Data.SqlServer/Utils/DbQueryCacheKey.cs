using Valtuutus.Data.Db;

namespace Valtuutus.Data.SqlServer.Utils;

/// <summary>
/// Identifies a distinct set of compiled, schema-qualified SQL for the SqlServer providers.
/// Queries are cached per key rather than in single-value statics so that multiple option sets
/// (e.g. different schemas / table names in a multi-tenant or multi-schema process) each get their
/// own correctly-qualified SQL instead of silently sharing whichever was built first.
/// </summary>
internal readonly record struct DbQueryCacheKey(
    string Schema,
    string TransactionsTable,
    string RelationsTable,
    string AttributesTable)
{
    public static DbQueryCacheKey From(IValtuutusDbOptions options) =>
        new(options.Schema, options.TransactionsTableName, options.RelationsTableName, options.AttributesTableName);
}
