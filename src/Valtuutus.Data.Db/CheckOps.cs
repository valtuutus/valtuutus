using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.Check;
using Valtuutus.Core.Engines.Check.V2;

namespace Valtuutus.Data.Db;

// Physical op for a fused sibling direct-relation batch — the declarative form of V1's runtime
// check-then-batch: one round trip answers what would otherwise be N sibling children.
internal sealed class HasAnyOfDirectRelationsOp(string[] relations, bool requireAll) : ICheckOp
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
}
