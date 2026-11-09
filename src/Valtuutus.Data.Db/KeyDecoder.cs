using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.ObjectPool;
using Valtuutus.Core;

namespace Valtuutus.Data.Db;

/// <summary>
/// Reads the key columns and attribute values of one result set. The rows of one read often repeat
/// a key in a column (the entity type, the relation, the subject type) and an attribute value (true,
/// a status), so the decoder hands back the string or parsed value it already made for them.
/// </summary>
/// <remarks>
/// With <c>keysAsUtf8</c>, each key field is read into a reused buffer as its UTF-8 bytes, and each of
/// the first ordinals keeps the bytes and string of its last key: a field with the same bytes returns
/// that string again without allocating. Providers whose driver can't return a text column's bytes
/// read keys as strings and share only the attribute values. A read rents a decoder with
/// <see cref="Rent"/> and disposes it to give it back.
/// </remarks>
public sealed class KeyDecoder : IDisposable
{
    private const int Slots = 8;
    private const int SlotBytes = 256;
    private const int RetainedBufferBytes = 4096;

    private static readonly ObjectPool<KeyDecoder> Utf8Pool = new DefaultObjectPool<KeyDecoder>(new Policy(true));
    private static readonly ObjectPool<KeyDecoder> StringPool = new DefaultObjectPool<KeyDecoder>(new Policy(false));

    private readonly bool _keysAsUtf8;
    private byte[] _buffer = new byte[SlotBytes];
    private readonly byte[] _lastBytes;
    private readonly int[] _lastLength = new int[Slots];
    private readonly string?[] _last = new string?[Slots];

    // Every row gets its own node over the one parsed element.
    private const int ValueSlots = 4;
    private const int CachedValueLength = 64;
    private readonly string?[] _rawValues = new string?[ValueSlots];
    private readonly JsonElement[] _values = new JsonElement[ValueSlots];
    private int _nextValueSlot;

    private KeyDecoder(bool keysAsUtf8)
    {
        _keysAsUtf8 = keysAsUtf8;
        _lastBytes = keysAsUtf8 ? new byte[Slots * SlotBytes] : [];
    }

    /// <summary>
    /// Rents a decoder. <paramref name="keysAsUtf8"/> reads key columns through
    /// <see cref="DbDataReader.GetBytes"/> as UTF-8, which the driver must support for them.
    /// </summary>
    public static KeyDecoder Rent(bool keysAsUtf8) => (keysAsUtf8 ? Utf8Pool : StringPool).Get();

    /// <summary>Gives the decoder back; it holds no key or value of the read that used it.</summary>
    public void Dispose() => (_keysAsUtf8 ? Utf8Pool : StringPool).Return(this);

    /// <summary>Reads the key at <paramref name="ordinal"/> of the current row.</summary>
    public string Read(DbDataReader reader, int ordinal)
    {
        if (!_keysAsUtf8)
            return reader.GetString(ordinal);

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

    /// <summary>Reads six key columns from ordinal 0 as a relation tuple.</summary>
    public RelationTuple ReadRelationTuple(DbDataReader reader) =>
        new(Read(reader, 0), Read(reader, 1), Read(reader, 2), Read(reader, 3), Read(reader, 4), Read(reader, 5));

    /// <summary>
    /// Reads three key columns from ordinal 0 and the attribute value at ordinal 3 as an attribute tuple.
    /// </summary>
    public AttributeTuple ReadAttributeTuple(DbDataReader reader) =>
        new(Read(reader, 0), Read(reader, 1), Read(reader, 2), ParseValue(reader.GetString(3)));

    /// <summary>Parses an attribute value; a JSON object, array or null fails the read.</summary>
    public JsonValue ParseValue(string raw)
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

    // A buffer an over-long field grew is dropped.
    private sealed class Policy(bool keysAsUtf8) : PooledObjectPolicy<KeyDecoder>
    {
        public override KeyDecoder Create() => new(keysAsUtf8);

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
