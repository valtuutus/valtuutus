using System.Data.Common;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.Db;

// Physical op for a fused sibling direct-relation batch — the declarative form of V1's runtime
// check-then-batch: one round trip answers what would otherwise be N sibling children.
internal sealed class HasAnyOfDirectRelationsOp(string[] relations, bool requireAll) : ICheckOp, IBatchableCheckOp
{
    public async ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx,
        string entityType, string entityId, CancellationToken ct)
    {
        if (reader is not IRelationalCheckOps ops)
            throw new InvalidOperationException(
                $"RelationalPlanRewriter installed a relational op, but the registered " +
                $"IDataReaderProvider ({reader.GetType().Name}) does not implement " +
                $"{nameof(IRelationalCheckOps)}. Readers used with AddDbSetup must implement it.");

        var matched = await ops.HasAnyOfDirectRelations(entityType, entityId, relations,
            ctx.SubjectId!, ctx.SnapToken, ct).ConfigureAwait(false);
        // Count checks are duplicate-safe: the return type is a set (a documented property of
        // HasAnyOfDirectRelations) and `relations` is duplicate-free by construction — a
        // repeated plan ref is memo-wrapped and never grouped.
        return requireAll ? matched.Count == relations.Length : matched.Count > 0;
    }

    public string Describe()
        => $"HasAnyOfDirectRelations([{string.Join(", ", relations)}], {(requireAll ? "all" : "any")})";

    public void AddToBatch(DbBatch batch, IRelationalBatchOps ops, CheckRequestContext ctx,
        string entityType, string entityId)
        => ops.AddHasAnyOfDirectRelationsToBatch(batch, entityType, entityId, relations, ctx.SubjectId!, ctx.SnapToken);

    public async Task ReadResultAsync(DbDataReader reader, int token, IOpCompletionSink sink, CancellationToken ct)
    {
        // Same shape as Execute's ops.HasAnyOfDirectRelations(...) call above — a single distinct-
        // relation column, deduplicated (it's already DISTINCT in SQL, but the set stays the
        // guard against any reader-level surprises) — then the same requireAll/any semantics.
        var matched = new HashSet<string>(relations.Length, StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            matched.Add(reader.GetString(0));
        sink.Complete(token, requireAll ? matched.Count == relations.Length : matched.Count > 0);
    }
}

// Symmetric to HasAnyOfDirectRelationsOp — R4's physical op for a fused sibling bool-attribute
// batch.
internal sealed class HasAnyOfAttributesOp(string[] attributes, bool requireAll) : ICheckOp, IBatchableCheckOp
{
    public async ValueTask<bool> Execute(IDataReaderProvider reader, CheckRequestContext ctx,
        string entityType, string entityId, CancellationToken ct)
    {
        if (reader is not IRelationalCheckOps ops)
            throw new InvalidOperationException(
                $"RelationalPlanRewriter installed a relational op, but the registered " +
                $"IDataReaderProvider ({reader.GetType().Name}) does not implement " +
                $"{nameof(IRelationalCheckOps)}. Readers used with AddDbSetup must implement it.");

        var matched = await ops.HasAnyOfAttributes(entityType, entityId, attributes,
            ctx.SnapToken, ct).ConfigureAwait(false);
        return requireAll ? matched.Count == attributes.Length : matched.Count > 0;
    }

    public string Describe()
        => $"HasAnyOfAttributes([{string.Join(", ", attributes)}], {(requireAll ? "all" : "any")})";

    public void AddToBatch(DbBatch batch, IRelationalBatchOps ops, CheckRequestContext ctx,
        string entityType, string entityId)
        => ops.AddHasAnyOfAttributesToBatch(batch, entityType, entityId, attributes, ctx.SnapToken);

    public async Task ReadResultAsync(DbDataReader reader, int token, IOpCompletionSink sink, CancellationToken ct)
    {
        var matched = new HashSet<string>(attributes.Length, StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            matched.Add(reader.GetString(0));
        sink.Complete(token, requireAll ? matched.Count == attributes.Length : matched.Count > 0);
    }
}
