using System.Collections;
using System.Runtime.InteropServices;

namespace Valtuutus.Core.Pools;

/// <summary>
/// A list rented from <see cref="ListPool{T}"/> that is returned to the pool on Dispose.
/// Use with <c>using var</c> for automatic return.
/// Call <see cref="Transfer"/> to take ownership of the underlying list without returning it to the pool.
/// </summary>
public readonly struct PooledList<T> : IEnumerable<T>, IDisposable
{
    private readonly List<T> _list;

    internal PooledList(List<T> list) => _list = list;

    /// <summary>
    /// Rents a <see cref="PooledList{T}"/> from the shared pool.
    /// </summary>
    public static PooledList<T> Rent() => new PooledList<T>(ListPool<T>.Rent());

    public int Count => _list.Count;
    public T this[int index] => _list[index];
    public List<T>.Enumerator GetEnumerator() => _list.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _list.GetEnumerator();
    public void Add(T item) => _list.Add(item);
    public void AddRange(IEnumerable<T> items) => _list.AddRange(items);

    /// <summary>
    /// Transfers ownership of the underlying list to the caller.
    /// The list is NOT returned to the pool — the caller is responsible for its lifecycle.
    /// </summary>
    public ReadOnlySpan<T> AsSpan() => CollectionsMarshal.AsSpan(_list);

    internal List<T> Transfer() => _list;

    public void Dispose() => ListPool<T>.Return(_list);
}
