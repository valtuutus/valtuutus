using Microsoft.Extensions.ObjectPool;

namespace Valtuutus.Core.Pools;

internal static class ListPool<T>
{
    private static readonly ObjectPool<List<T>> _pool =
        new DefaultObjectPool<List<T>>(new ListPooledObjectPolicy<T>());

    public static List<T> Rent() => _pool.Get();

    public static void Return(List<T> list) => _pool.Return(list);
}

file sealed class ListPooledObjectPolicy<T> : PooledObjectPolicy<List<T>>
{
    public override List<T> Create() => new();

    public override bool Return(List<T> obj)
    {
        obj.Clear();
        return true;
    }
}
