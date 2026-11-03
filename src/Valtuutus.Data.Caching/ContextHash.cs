using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Valtuutus.Data.Caching;

/// <summary>
/// Cache key segment identifying a request context. Formats as nothing for an empty context,
/// otherwise as <c>:</c> followed by the hex hash, written straight into the key being built.
/// </summary>
internal readonly struct ContextKey : ISpanFormattable
{
    private const int HexLength = 1 + 2 * ContextHash.HashBytes;

    private readonly ulong _high;
    private readonly ulong _low;
    private readonly bool _hasValue;

    public ContextKey(ulong high, ulong low)
    {
        _high = high;
        _low = low;
        _hasValue = true;
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (!_hasValue)
        {
            charsWritten = 0;
            return true;
        }

        if (destination.Length < HexLength)
        {
            charsWritten = 0;
            return false;
        }

        destination[0] = ':';
        WriteHex(destination.Slice(1, 16), _high);
        WriteHex(destination.Slice(17, 16), _low);
        charsWritten = HexLength;
        return true;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public override string ToString() => string.Create(_hasValue ? HexLength : 0, this,
        static (span, key) => key.TryFormat(span, out _, default, null));

    private static void WriteHex(Span<char> destination, ulong value)
    {
        for (var i = 15; i >= 0; i--)
        {
            destination[i] = "0123456789ABCDEF"[(int)(value & 0xF)];
            value >>= 4;
        }
    }
}

/// <summary>
/// Hashes a request context into a <see cref="ContextKey"/>. Requests whose contexts differ in any
/// key, value or value type get different keys, so a context-dependent result is never served to
/// another context.
/// </summary>
internal static class ContextHash
{
    private const byte NullTag = 0;
    private const byte StringTag = 1;
    private const byte IntTag = 2;
    private const byte DecimalTag = 3;
    private const byte BooleanTag = 4;

    // Context values can come from end users, so the hash must resist crafted collisions: a
    // permitted context colliding with a denied one would serve the cached allow. Truncating
    // SHA-256 to 128 bits keeps that resistance with a shorter key.
    internal const int HashBytes = 16;

    // Contexts are small; larger ones rent their buffers from the shared pools.
    private const int StackBufferBytes = 512;

    /// <summary>
    /// Builds the key for <paramref name="context"/>. Returns false when a value has a type the
    /// engine does not read from context, in which case the request is not cacheable.
    /// </summary>
    public static bool TryGetKey(IDictionary<string, object> context, out ContextKey key)
    {
        key = default;
        var count = context.Count;
        if (count == 0) return true;

        var keys = ArrayPool<string>.Shared.Rent(count);
        try
        {
            context.Keys.CopyTo(keys, 0);
            var sorted = keys.AsSpan(0, count);
            if (count > 1) sorted.Sort(static (a, b) => string.CompareOrdinal(a, b));

            var size = 0;
            foreach (var name in sorted)
            {
                if (!TryMaxValueSize(context[name], out var valueSize)) return false;
                size += sizeof(int) + Encoding.UTF8.GetMaxByteCount(name.Length) + 1 + valueSize;
            }

            byte[]? rented = null;
            Span<byte> buffer = size <= StackBufferBytes
                ? stackalloc byte[StackBufferBytes]
                : rented = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                var written = 0;
                foreach (var name in sorted)
                {
                    written += WriteString(buffer[written..], name);
                    written += WriteValue(buffer[written..], context[name]);
                }

                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(buffer[..written], hash);
                key = new ContextKey(
                    BinaryPrimitives.ReadUInt64BigEndian(hash),
                    BinaryPrimitives.ReadUInt64BigEndian(hash[8..]));
                return true;
            }
            finally
            {
                if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
            }
        }
        finally
        {
            ArrayPool<string>.Shared.Return(keys, clearArray: true);
        }
    }

    private static bool TryMaxValueSize(object? value, out int size)
    {
        size = value switch
        {
            null => 0,
            string s => sizeof(int) + Encoding.UTF8.GetMaxByteCount(s.Length),
            int => sizeof(int),
            decimal => 4 * sizeof(int),
            bool => 1,
            _ => -1
        };
        return size >= 0;
    }

    private static int WriteValue(Span<byte> dest, object? value)
    {
        switch (value)
        {
            case null:
                dest[0] = NullTag;
                return 1;
            case string s:
                dest[0] = StringTag;
                return 1 + WriteString(dest[1..], s);
            case int i:
                dest[0] = IntTag;
                BinaryPrimitives.WriteInt32LittleEndian(dest[1..], i);
                return 1 + sizeof(int);
            case decimal d:
                dest[0] = DecimalTag;
                Span<int> bits = stackalloc int[4];
                decimal.GetBits(d, bits);
                for (var b = 0; b < 4; b++)
                    BinaryPrimitives.WriteInt32LittleEndian(dest[(1 + b * sizeof(int))..], bits[b]);
                return 1 + 4 * sizeof(int);
            case bool b:
                dest[0] = BooleanTag;
                dest[1] = b ? (byte)1 : (byte)0;
                return 2;
            default:
                throw new NotSupportedException($"Unsupported context value type {value.GetType()}.");
        }
    }

    // Length-prefixed, so adjacent strings cannot run into each other.
    private static int WriteString(Span<byte> dest, string value)
    {
        var length = Encoding.UTF8.GetBytes(value, dest[sizeof(int)..]);
        BinaryPrimitives.WriteInt32LittleEndian(dest, length);
        return sizeof(int) + length;
    }
}
