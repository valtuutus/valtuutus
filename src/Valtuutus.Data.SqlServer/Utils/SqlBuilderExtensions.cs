using Dapper;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.SqlServer.Utils;

internal static class SqlBuilderExtensions
{
    internal const string TvpListIds = "TVP_ListIds";

    // SQL Server resolves unqualified UDTT names against the connection's default schema,
    // not the configured ValtuutusSqlServerOptions.Schema. Always pass the schema-qualified
    // type name (e.g. "[valtuutus].[TVP_ListIds]") to AsTableValuedParameter.
    internal static string FormatTvpListIdsName(string schema) => $"[{schema}].[{TvpListIds}]";
}

internal static class CommonSqlBuilderExtensions
{
    private const string SnapTokenFilter = "created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)";

    public static SqlBuilder ApplySnapTokenFilter(SqlBuilder builder, SnapToken snapToken)
    {
        return builder.Where(SnapTokenFilter, new
        {
            SnapToken = new DbString
            {
                Value = snapToken.Value,
                Length = 26,
                IsFixedLength = true,
            }
        });
    }
}
