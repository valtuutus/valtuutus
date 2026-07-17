using Microsoft.Extensions.ObjectPool;
using Valtuutus.Core.Data;
using Valtuutus.Core.Schemas;

namespace Valtuutus.Core.Engines.Check.V2;

// Registered as a DI singleton alongside CheckPlanCache: schema/plans are fixed for the
// container's lifetime, so it's safe to bake them into the pool's policy. Physical (which
// wraps the per-request scoped reader) is set fresh on every Rent, never pooled — and
// ExecuteAsync itself resets every other piece of mutable state (frames, ready-list, memo,
// mailbox) at the start of each call, so a pooled instance is always safe to reuse regardless
// of whether its previous use succeeded, faulted, or was cancelled.
internal sealed class CheckPlanExecutorPool(Schema schema, CheckPlanCache plans)
{
    private readonly ObjectPool<CheckPlanExecutor> _executors =
        new DefaultObjectPool<CheckPlanExecutor>(new ExecutorPolicy(schema, plans));
    private readonly ObjectPool<DefaultPhysicalExecutor> _physicals =
        new DefaultObjectPool<DefaultPhysicalExecutor>(new PhysicalPolicy(schema));

    public CheckPlanExecutor Rent(IDataReaderProvider reader)
    {
        var physical = _physicals.Get();
        physical.Reader = reader;
        var executor = _executors.Get();
        executor.Physical = physical;
        return executor;
    }

    // Only ever called with an executor obtained from Rent (see above), which always sets
    // Physical to a pooled DefaultPhysicalExecutor — the cast is safe in that path. Tests that
    // construct CheckPlanExecutor directly (bypassing this pool) never call Return on it.
    public void Return(CheckPlanExecutor executor)
    {
        _physicals.Return((DefaultPhysicalExecutor)executor.Physical);
        _executors.Return(executor);
    }

    private sealed class ExecutorPolicy(Schema schema, CheckPlanCache plans) : PooledObjectPolicy<CheckPlanExecutor>
    {
        public override CheckPlanExecutor Create() => new(schema, plans);

        public override bool Return(CheckPlanExecutor executor) => true;
    }

    private sealed class PhysicalPolicy(Schema schema) : PooledObjectPolicy<DefaultPhysicalExecutor>
    {
        public override DefaultPhysicalExecutor Create() => new(schema);

        public override bool Return(DefaultPhysicalExecutor physical) => true;
    }
}
