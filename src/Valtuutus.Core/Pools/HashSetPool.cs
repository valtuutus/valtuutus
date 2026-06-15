using Microsoft.Extensions.ObjectPool;

namespace Valtuutus.Core.Pools;

internal static class HashSetPool<T>
{
    private static readonly ObjectPool<HashSet<T>> _pool =
        new DefaultObjectPool<HashSet<T>>(new HashSetPooledObjectPolicy<T>());

    public static HashSet<T> Rent() => _pool.Get();

    public static void Return(HashSet<T> set)
        => _pool.Return(set);
}

file class HashSetPooledObjectPolicy<T> : PooledObjectPolicy<HashSet<T>>
{
    public override HashSet<T> Create() => new();

    public override bool Return(HashSet<T> obj)
    {
        obj.Clear();
        return true;
    }
}

internal readonly ref struct PooledHashSet<T> : IDisposable
{
    internal readonly HashSet<T> Set;

    internal PooledHashSet(HashSet<T> set) => Set = set;

    public void Dispose() => HashSetPool<T>.Return(Set);

    public static PooledHashSet<T> Rent() =>
        new(HashSetPool<T>.Rent());
}
