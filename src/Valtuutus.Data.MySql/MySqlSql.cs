using System.Buffers;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.ObjectPool;
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


    // Duplicate values would duplicate the rows a JSON_TABLE join returns, so each value is written
    // once, in first-seen order, escaped by Utf8JsonWriter's default encoder. The writer, its buffer
    // and the seen-set are kept per thread for the next array, unless this one grew them past the
    // retention limits.
    internal static string ToJsonArray(IEnumerable<string> values)
    {
        var state = JsonArrayState.Cached ?? new JsonArrayState();
        JsonArrayState.Cached = null;
        try
        {
            var writer = state.Writer;
            writer.WriteStartArray();
            // The common collections are walked through their own enumerators, so no enumerator is
            // boxed. A set that compares ordinally already holds each value once.
            switch (values)
            {
                case string[] array:
                    foreach (var value in array)
                        state.WriteDistinct(value);
                    break;
                case List<string> list:
                    foreach (var value in list)
                        state.WriteDistinct(value);
                    break;
                case HashSet<string> set when IsOrdinal(set.Comparer):
                    foreach (var value in set)
                        writer.WriteStringValue(value);
                    break;
                default:
                    foreach (var value in values)
                        state.WriteDistinct(value);
                    break;
            }
            writer.WriteEndArray();
            writer.Flush();
            return Encoding.UTF8.GetString(state.Buffer.WrittenSpan);
        }
        finally
        {
            if (state.Reset())
                JsonArrayState.Cached = state;
        }
    }

    private sealed class JsonArrayState
    {
        private const int RetainedBufferBytes = 64 * 1024;
        private const int RetainedSeenCount = 1024;

        [ThreadStatic] internal static JsonArrayState? Cached;

        internal readonly ArrayBufferWriter<byte> Buffer = new(256);
        internal readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        internal readonly Utf8JsonWriter Writer;

        internal JsonArrayState() => Writer = new Utf8JsonWriter(Buffer);

        internal void WriteDistinct(string value)
        {
            if (Seen.Add(value))
                Writer.WriteStringValue(value);
        }

        // True when the state is small enough to keep for the next array.
        internal bool Reset()
        {
            var keep = Buffer.Capacity <= RetainedBufferBytes && Seen.Count <= RetainedSeenCount;
            Seen.Clear();
            Buffer.ResetWrittenCount();
            Writer.Reset(Buffer);
            return keep;
        }
    }

    // Collapses the indentation and newlines of a multi-line template into single spaces. The text is
    // sent to the server on every execution, so it is compacted once, when the catalog is built.
    internal static string Compact(string sql) =>
        System.Text.RegularExpressions.Regex.Replace(sql.Trim(), @"\s+", " ");

    // Parameters are bound under their bare names, which the statement text reads with an @ prefix.
    // MySqlConnector matches either form, and keeps a bare name as it is instead of storing a copy
    // without the prefix.
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
            parameters.Add(new MySqlParameter("snap_token", MySqlDbType.String) { Size = 26, Value = token.Value });
    }

    internal static void AddJsonArray(DbParameterCollection parameters, string name, IEnumerable<string> values) =>
        parameters.Add(new MySqlParameter(name, MySqlDbType.JSON) { Value = ToJsonArray(values) });

    // One value binds to the equality form's scalar parameter, any other count to the JSON form's array.
    internal static void AddOneOrMany(DbParameterCollection parameters, string scalarName, string arrayName,
        IReadOnlyCollection<string> values, int scalarSize)
    {
        if (values.Count == 1)
            AddString(parameters, scalarName, Single(values), scalarSize);
        else
            AddJsonArray(parameters, arrayName, values);
    }

    // The value of a one-value collection, read without boxing an enumerator for the common ones.
    internal static string Single(IReadOnlyCollection<string> values)
    {
        switch (values)
        {
            case IReadOnlyList<string> list:
                return list[0];
            case HashSet<string> set:
                foreach (var value in set)
                    return value;
                break;
        }
        return values.First();
    }

    private static bool IsOrdinal(IEqualityComparer<string> comparer) =>
        ReferenceEquals(comparer, EqualityComparer<string>.Default) || ReferenceEquals(comparer, StringComparer.Ordinal);

    // The engine hands ids over as read-only collections; anything else is copied once.
    internal static IReadOnlyCollection<string> AsCollection(IEnumerable<string> values) =>
        values as IReadOnlyCollection<string> ?? values.ToArray();
}

/// <summary>
/// Decodes the VARBINARY key columns, which come back as bytes, as UTF-8. Each field is copied into
/// a buffer the decoder reuses. The rows of one read often repeat a key in a column (the entity
/// type, the relation, the subject type), so each of the first ordinals keeps the bytes and string
/// of the last key it decoded, and a field with the same bytes returns that string again. A read
/// rents a decoder through <see cref="Rent"/> and gives it back through <see cref="Return"/>.
/// </summary>
internal sealed class KeyDecoder
{
    private const int Slots = 8;
    private const int SlotBytes = 256;
    private const int RetainedBufferBytes = 4096;

    private static readonly ObjectPool<KeyDecoder> Pool = new DefaultObjectPool<KeyDecoder>(new Policy());

    private byte[] _buffer = new byte[SlotBytes];
    private readonly byte[] _lastBytes = new byte[Slots * SlotBytes];
    private readonly int[] _lastLength = new int[Slots];
    private readonly string?[] _last = new string?[Slots];

    // The last attribute values parsed, by their raw text. Attribute rows often repeat a value
    // (true, a status), and every row gets its own node over the one parsed element.
    private const int ValueSlots = 4;
    private const int CachedValueLength = 64;
    private readonly string?[] _rawValues = new string?[ValueSlots];
    private readonly JsonElement[] _values = new JsonElement[ValueSlots];
    private int _nextValueSlot;

    internal static KeyDecoder Rent() => Pool.Get();

    internal static void Return(KeyDecoder decoder) => Pool.Return(decoder);

    internal string Read(DbDataReader reader, int ordinal)
    {
        var length = (int)reader.GetBytes(ordinal, 0, null, 0, 0);
        if (length == 0)
            return "";
        if (length > _buffer.Length)
            _buffer = new byte[Math.Max(length, _buffer.Length * 2)];
        reader.GetBytes(ordinal, 0, _buffer, 0, length);
        var bytes = _buffer.AsSpan(0, length);
        if ((uint)ordinal >= Slots || length > SlotBytes)
            return Encoding.UTF8.GetString(bytes);

        var slot = _lastBytes.AsSpan(ordinal * SlotBytes, length);
        if (_last[ordinal] is { } last && _lastLength[ordinal] == length && bytes.SequenceEqual(slot))
            return last;
        var key = Encoding.UTF8.GetString(bytes);
        bytes.CopyTo(slot);
        _lastLength[ordinal] = length;
        _last[ordinal] = key;
        return key;
    }

    // An attribute value is a JSON scalar; an object, array or null fails the read.
    internal JsonValue ParseValue(string raw)
    {
        for (var i = 0; i < ValueSlots; i++)
        {
            if (string.Equals(_rawValues[i], raw, StringComparison.Ordinal))
                return JsonValue.Create(_values[i])!;
        }
        var value = JsonNode.Parse(raw) as JsonValue
            ?? throw new InvalidOperationException("Attribute value must deserialize to a JsonValue.");
        if (raw.Length <= CachedValueLength && value.TryGetValue<JsonElement>(out var element))
        {
            _rawValues[_nextValueSlot] = raw;
            _values[_nextValueSlot] = element;
            _nextValueSlot = (_nextValueSlot + 1) % ValueSlots;
        }
        return value;
    }

    // A returned decoder holds no key or value of the read that used it, and a buffer an over-long
    // field grew is dropped.
    private sealed class Policy : PooledObjectPolicy<KeyDecoder>
    {
        public override KeyDecoder Create() => new();

        public override bool Return(KeyDecoder decoder)
        {
            Array.Clear(decoder._last);
            Array.Clear(decoder._rawValues);
            Array.Clear(decoder._values);
            if (decoder._buffer.Length > RetainedBufferBytes)
                decoder._buffer = new byte[SlotBytes];
            return true;
        }
    }
}
