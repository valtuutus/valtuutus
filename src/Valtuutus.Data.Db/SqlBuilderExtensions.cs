using Dapper;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.Db;

public static class CommonSqlBuilderExtensions
{
    private const string SnapTokenFilter = "created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)";

    public static SqlBuilder ApplySnapTokenFilter(SqlBuilder builder, SnapToken snapToken)
    {
        return builder.Where(SnapTokenFilter, new { SnapToken = new DbString
        {
            Value = snapToken.Value,
            Length = 26,
            IsFixedLength = true,
        }});
    }
}
