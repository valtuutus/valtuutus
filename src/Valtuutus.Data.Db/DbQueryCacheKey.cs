namespace Valtuutus.Data.Db;

internal readonly record struct DbQueryCacheKey(
    string Schema,
    string TransactionsTable,
    string RelationsTable,
    string AttributesTable)
{
    public static DbQueryCacheKey From(IValtuutusDbOptions options) =>
        new(options.Schema, options.TransactionsTableName, options.RelationsTableName, options.AttributesTableName);
}
