using Microsoft.Extensions.ObjectPool;

namespace Valtuutus.Core.Pools;

internal static class DictionaryPool<TKey, TValue> where TKey : notnull
{
    private static readonly ObjectPool<Dictionary<TKey, TValue>> _pool =
        new DefaultObjectPool<Dictionary<TKey, TValue>>(new DictionaryPooledObjectPolicy<TKey, TValue>());

    public static Dictionary<TKey, TValue> Rent() => _pool.Get();

    public static void Return(Dictionary<TKey, TValue> dictionary)
        => _pool.Return(dictionary);
}

file class DictionaryPooledObjectPolicy<TKey, TValue> : PooledObjectPolicy<Dictionary<TKey, TValue>> where TKey : notnull
{
    public override Dictionary<TKey, TValue> Create() => new();

    public override bool Return(Dictionary<TKey, TValue> obj)
    {
        obj.Clear();
        return true;
    }
}

internal readonly ref struct PooledDictionary<TKey, TValue> where TKey : notnull
{
    internal readonly Dictionary<TKey, TValue> Dictionary;

    internal PooledDictionary(Dictionary<TKey, TValue> dict) => Dictionary = dict;

    public void Dispose() => DictionaryPool<TKey, TValue>.Return(Dictionary);

    public static PooledDictionary<TKey, TValue> Rent() =>
        new(DictionaryPool<TKey, TValue>.Rent());
}