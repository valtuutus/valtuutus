using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Valtuutus.Data.Caching;

/// <summary>
/// Hashes a request context into a cache key segment. Requests whose contexts differ in any key,
/// value or value type get different segments, so a context-dependent result is never served to
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
    private const int HashBytes = 16;

    /// <summary>
    /// Returns the cache key suffix for <paramref name="context"/>: empty for an empty context,
    /// otherwise <c>:</c> followed by the hash. Returns false when a value has a type the
    /// engine does not read from context, in which case the request is not cacheable.
    /// </summary>
    public static bool TryGetKeySuffix(IDictionary<string, object> context, out string suffix)
    {
        if (context.Count == 0)
        {
            suffix = "";
            return true;
        }

        var keys = new string[context.Count];
        context.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);

        var size = 0;
        foreach (var key in keys)
        {
            if (!TryMaxValueSize(context[key], out var valueSize))
            {
                suffix = "";
                return false;
            }
            size += sizeof(int) + Encoding.UTF8.GetMaxByteCount(key.Length) + 1 + valueSize;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var written = 0;
            foreach (var key in keys)
            {
                written += WriteString(buffer.AsSpan(written), key);
                written += WriteValue(buffer.AsSpan(written), context[key]);
            }

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(buffer.AsSpan(0, written), hash);
            suffix = ":" + Convert.ToHexString(hash[..HashBytes]);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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
