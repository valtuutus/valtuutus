using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace Valtuutus.Data.SqlServer.Utils;

internal static class TvpHelper
{
    internal const string TvpListIds = "TVP_ListIds";

    internal static IEnumerable<SqlDataRecord> CreateIdTvp(IEnumerable<string> values)
    {
        var record = new SqlDataRecord(_tvpListIdsMeta);

        foreach (var value in values)
        {
            record.SetString(0, value);
            yield return record;
        }
    }

    internal static SqlParameter CreateTvpParameter(string parameterName, IEnumerable<string> values, string typeName) =>
        new()
        {
            ParameterName = parameterName,
            SqlDbType = SqlDbType.Structured,
            TypeName = typeName,
            Value = CreateIdTvp(values)
        };

    private static readonly SqlMetaData[] _tvpListIdsMeta = new SqlMetaData[] { new("id", SqlDbType.NVarChar, 256) };
}