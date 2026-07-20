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
internal sealed class CheckPlanExecutorPool(Schema schema, CheckPlanCache plans,
    Func<Schema, IPhysicalExecutor> physicalExecutorFactory)
{
    private readonly ObjectPool<CheckPlanExecutor> _executors =
        new DefaultObjectPool<CheckPlanExecutor>(new ExecutorPolicy(schema, plans));
    // Factory-driven so a relational provider (Valtuutus.Data.Db) can swap in a batching
    // IPhysicalExecutor without Core knowing anything about ADO.NET or DbBatch — Core only
    // knows the IPhysicalExecutor contract, exactly like it already only knows IDataReaderProvider
    // and never a concrete provider type.
    private readonly ObjectPool<IPhysicalExecutor> _physicals =
        new DefaultObjectPool<IPhysicalExecutor>(new PhysicalPolicy(schema, physicalExecutorFactory));

    public CheckPlanExecutor Rent(IDataReaderProvider reader)
    {
        var physical = _physicals.Get();
        physical.Reader = reader;
        var executor = _executors.Get();
        executor.Physical = physical;
        return executor;
    }

    public void Return(CheckPlanExecutor executor)
    {
        _physicals.Return(executor.Physical);
        _executors.Return(executor);
    }

    private sealed class ExecutorPolicy(Schema schema, CheckPlanCache plans) : PooledObjectPolicy<CheckPlanExecutor>
    {
        public override CheckPlanExecutor Create() => new(schema, plans);

        public override bool Return(CheckPlanExecutor executor) => true;
    }

    private sealed class PhysicalPolicy(Schema schema, Func<Schema, IPhysicalExecutor> factory)
        : PooledObjectPolicy<IPhysicalExecutor>
    {
        public override IPhysicalExecutor Create() => factory(schema);

        public override bool Return(IPhysicalExecutor physical) => true;
    }
}
