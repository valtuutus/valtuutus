using Microsoft.Extensions.ObjectPool;

namespace Valtuutus.Core.Pools;

internal static class CancellationTokenSourcePool
{
    private static readonly ObjectPool<CancellationTokenSource> _pool =
        new DefaultObjectPool<CancellationTokenSource>(new CtsPooledObjectPolicy());

    public static PooledCancellationTokenSource Rent(CancellationToken linkedToken)
    {
        var cts = _pool.Get();
        // Create a linked registration so that cancelling the outer token cancels ours too
        var reg = linkedToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), cts);
        return new PooledCancellationTokenSource(cts, reg);
    }

    internal static void Return(CancellationTokenSource cts) => _pool.Return(cts);
}

file class CtsPooledObjectPolicy : PooledObjectPolicy<CancellationTokenSource>
{
    public override CancellationTokenSource Create() => new();

    public override bool Return(CancellationTokenSource obj)
    {
        if (obj.IsCancellationRequested)
        {
            // Reset so it can be reused
            try { obj.TryReset(); }
            catch { return false; }
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

    public void Cancel() => _cts.Cancel();

    public void Dispose()
    {
        _registration.Dispose();
        CancellationTokenSourcePool.Return(_cts);
    }
}
