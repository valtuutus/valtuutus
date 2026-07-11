namespace Valtuutus.Data.SqlServer.Utils;

internal static class SqlBuilderExtensions
{
    internal const string TvpListIds = "TVP_ListIds";

    // SQL Server resolves unqualified UDTT names against the connection's default schema,
    // not the configured ValtuutusSqlServerOptions.Schema. Always pass the schema-qualified
    // type name (e.g. "[valtuutus].[TVP_ListIds]") to AsTableValuedParameter.
    internal static string FormatTvpListIdsName(string schema) => $"[{schema}].[{TvpListIds}]";
}
