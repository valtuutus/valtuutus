using System.Buffers;
using Valtuutus.Core.Lang;

namespace Valtuutus.Core.Pools;

/// <summary>
/// ArrayPool-backed buffer for a function call's positional LiteralValueUnion arguments. Not a
/// stackalloc Span because LiteralValueUnion carries a `string?` field, making it a managed type
/// -- stackalloc is illegal for managed types (confirmed via CS0208 during the #275 spike).
/// </summary>
internal readonly struct PooledLiteralValueArray : IDisposable
{
    private readonly LiteralValueUnion[] _array;
    private readonly int _length;

    private PooledLiteralValueArray(LiteralValueUnion[] array, int length)
    {
        _array = array;
        _length = length;
    }

    public Span<LiteralValueUnion> Span => _array.AsSpan(0, _length);

    public static PooledLiteralValueArray Rent(int length) =>
        new(ArrayPool<LiteralValueUnion>.Shared.Rent(length), length);

    public void Dispose() => ArrayPool<LiteralValueUnion>.Shared.Return(_array, clearArray: true);
}
