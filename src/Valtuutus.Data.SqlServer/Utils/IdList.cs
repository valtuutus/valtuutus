using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Valtuutus.Data.SqlServer.Utils;

/// <summary>
/// A list of ids or names passed as one nvarchar(max) parameter holding a JSON array, and read
/// back as rows with OPENJSON. The rows' <c>id</c> column is nvarchar(64), the type of every
/// column a list is compared with, so comparisons need no conversion on the indexed side.
/// </summary>
internal static class IdList
{
    /// <summary>
    /// The query reading the list in <paramref name="parameterName"/> as rows with one <c>id</c>
    /// column, for use as <c>col IN (...)</c>.
    /// </summary>
    internal static string From(string parameterName) =>
        $"SELECT id FROM OPENJSON({parameterName}) WITH (id nvarchar(64) '$')";

    internal static void AddParameter(SqlParameterCollection parameters, string name, IEnumerable<string> values) =>
        parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, -1)
        {
            Value = JsonSerializer.Serialize(values, IdListJsonContext.Default.IEnumerableString)
        });
}

[JsonSerializable(typeof(IEnumerable<string>))]
internal sealed partial class IdListJsonContext : JsonSerializerContext;
