using Microsoft.Extensions.ObjectPool;

namespace Valtuutus.Core.Pools;

internal static class CancellationTokenSourcePool
{
    private static readonly ObjectPool<CancellationTokenSource> _pool =
        new DefaultObjectPool<CancellationTokenSource>(new CtsPooledObjectPolicy());

    public static PooledCancellationTokenSource Rent(CancellationToken linkedToken)
    {
        var cts = _pool.Get();
        var reg = linkedToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), cts);
        return new PooledCancellationTokenSource(cts, reg);
    }

    internal static void Return(CancellationTokenSource cts) => _pool.Return(cts);
}

file sealed class CtsPooledObjectPolicy : PooledObjectPolicy<CancellationTokenSource>
{
    public override CancellationTokenSource Create() => new();

    public override bool Return(CancellationTokenSource obj)
    {
        if (obj.IsCancellationRequested)
        {
#if NET6_0_OR_GREATER
            try { obj.TryReset(); }
            catch (Exception) { return false; }
#else
            return false;
#endif
        }
        return !obj.IsCancellationRequested;
    }
}

internal readonly struct PooledCancellationTokenSource : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenRegistration _registration;

    internal PooledCancellationTokenSource(CancellationTokenSource cts, CancellationTokenRegistration registration)
    {
        _cts = cts;
        _registration = registration;
    }

    public CancellationToken Token => _cts.Token;
    internal CancellationTokenSource InnerSource => _cts;

    public void Cancel() => _cts.Cancel();

    public void Dispose()
    {
        _registration.Dispose();
        CancellationTokenSourcePool.Return(_cts);
    }
}
