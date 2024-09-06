using Dapper;
using Valtuutus.Core.Engines;

namespace Valtuutus.Data.Db;

public static class CommonSqlBuilderExtensions
{
    private const string SnapTokenFilter = "created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)";

    public static SqlBuilder ApplySnapTokenFilter<T>(SqlBuilder builder, T withSnapToken) where T: IWithSnapToken
    {
        if (withSnapToken.SnapToken != null)
        {
            var parameters = new { SnapToken = new DbString()
            {
                Value = withSnapToken.SnapToken.Value.Value,
                Length = 26,
                IsFixedLength = true,
            }};
            builder = builder.Where(
                SnapTokenFilter,
                parameters);
        }
        return builder;
    }
}