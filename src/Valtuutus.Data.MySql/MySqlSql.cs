using System.Buffers;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySqlConnector;
using Valtuutus.Core.Data;

namespace Valtuutus.Data.MySql;

internal static class MySqlSql
{
    internal const string Cols = "entity_type, entity_id, relation, subject_type, subject_id, subject_relation";

    internal static string ColsOf(string alias) =>
        $"{alias}.entity_type, {alias}.entity_id, {alias}.relation, {alias}.subject_type, {alias}.subject_id, {alias}.subject_relation";

    internal static string AttrColsOf(string alias) =>
        $"{alias}.entity_type, {alias}.entity_id, {alias}.attribute, {alias}.value";

    internal static string Snap(string alias, string snap) =>
        $"{alias}.created_tx_id <= {snap} AND ({alias}.deleted_tx_id IS NULL OR {alias}.deleted_tx_id > {snap})";

    // MySQL estimates every equality seek with index dives on each execution. On a single-table,
    // equality-only block the plan can't change, so the dives are skipped. Multi-table statements
    // keep them: the join order depends on them.
    internal static string PointHint(MySqlServerType serverType, string alias) =>
        serverType == MySqlServerType.MySql ? $"/*+ NO_RANGE_OPTIMIZATION({alias}) */ " : "";

    // Byte widths of the VARBINARY key columns.
    internal const int TypeBytes = 256;
    internal const int IdBytes = 256;
    internal const int NameBytes = 64;

    // MariaDB cuts a JSON_TABLE value longer than its column down to the column's width, and the cut
    // value could equal a stored key. A JSON_TABLE key column is therefore declared one byte wider
    // than the table column it is compared to: whatever it holds after a cut is longer than any
    // stored key, so it matches nothing. MySQL returns NULL for an over-long value instead.
    internal static string JsonColumn(string name, int tableColumnBytes) =>
        $"{name} VARBINARY({tableColumnBytes + 1})";

    // Compared to entity_id or subject_id.
    internal static string JsonIds(string parameter, string alias) =>
        $"JSON_TABLE({parameter}, '$[*]' COLUMNS ({JsonColumn("id", IdBytes)} PATH '$')) {alias}";

    // Compared to relation or attribute.
    internal static string JsonNames(string parameter, string alias) =>
        $"JSON_TABLE({parameter}, '$[*]' COLUMNS ({JsonColumn("name", NameBytes)} PATH '$')) {alias}";

    // Key columns are VARBINARY and come back as bytes. They are copied into a buffer the caller
    // reuses across rows, so decoding allocates only the string. The buffer comes from RentKeyBuffer
    // and goes back through ReturnKeyBuffer.
    internal static string ReadKey(DbDataReader reader, int ordinal, ref byte[] buffer)
    {
        var length = (int)reader.GetBytes(ordinal, 0, null, 0, 0);
        if (length == 0)
            return "";
        if (length > buffer.Length)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(length);
        }
        reader.GetBytes(ordinal, 0, buffer, 0, length);
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    internal static byte[] RentKeyBuffer() => ArrayPool<byte>.Shared.Rent(TypeBytes);

    internal static void ReturnKeyBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);

    // Duplicate values would duplicate the rows a JSON_TABLE join returns.
    internal static string ToJsonArray(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.Distinct(StringComparer.Ordinal).ToArray(), MySqlJsonContext.Default.StringArray);

    // Collapses the indentation and newlines of a multi-line template into single spaces. The text is
    // sent to the server on every execution, so it is compacted once, when the catalog is built.
    internal static string Compact(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql.Trim(), @"\s+", " ");

    internal static void AddString(DbParameterCollection parameters, string name, string value, int size) =>
        parameters.Add(new MySqlParameter(name, MySqlDbType.VarChar) { Size = size, Value = value });

    // Blank values bind as NULL, which the "@p IS NULL OR col = @p" filters read as "any".
    internal static void AddNullableString(DbParameterCollection parameters, string name, string? value, int size) =>
        parameters.Add(new MySqlParameter(name, MySqlDbType.VarChar)
        {
            Size = size,
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value
        });

    // A null token writes none: statements that read a batch-resolved token have no parameter for it.
    internal static void AddSnapToken(DbParameterCollection parameters, SnapToken? snapToken)
    {
        if (snapToken is { } token)
            parameters.Add(new MySqlParameter("@snap_token", MySqlDbType.String) { Size = 26, Value = token.Value });
    }

    internal static void AddJsonArray(DbParameterCollection parameters, string name, IEnumerable<string> values) =>
        parameters.Add(new MySqlParameter(name, MySqlDbType.JSON) { Value = ToJsonArray(values) });
}

[JsonSerializable(typeof(string[]))]
internal partial class MySqlJsonContext : JsonSerializerContext;
