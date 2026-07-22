using Valtuutus.Core.Data;

namespace Valtuutus.Data.Db;

/// <summary>
/// Relational op catalog for the V2 check engine's plan rewriter (<c>RelationalPlanRewriter</c>).
/// Relational providers (Postgres, SqlServer) implement this alongside
/// <see cref="IDataReaderProvider"/> — today its members are a subset of that interface with
/// identical signatures, so implementing it is declaration-only. When the core reader interface
/// eventually shrinks to its primitive core (a major-version event), the specialized members
/// survive here instead of vanishing. New rewrite rules grow this catalog — growth here reaches
/// only the relational providers, never the core reader interface.
/// </summary>
public interface IRelationalCheckOps
{
    /// <inheritdoc cref="IDataReaderProvider.HasAnyOfDirectRelations"/>
    Task<HashSet<string>> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken);

    /// <inheritdoc cref="IDataReaderProvider.HasAnyOfAttributes"/>
    Task<HashSet<string>> HasAnyOfAttributes(string entityType, string entityId, string[] attributeNames,
        SnapToken snapToken, CancellationToken cancellationToken);

    /// <summary>
    /// The userset 2-hop join fast path: true iff <paramref name="subjectId"/> either holds
    /// <paramref name="relation"/> directly on (<paramref name="entityType"/>,
    /// <paramref name="entityId"/>), or holds <paramref name="computedRelation"/> on
    /// (<paramref name="subEntityType"/>, X) for some X that (<paramref name="entityType"/>,
    /// <paramref name="entityId"/>) references via <paramref name="relation"/> as a userset
    /// (<c>@subEntityType#computedRelation</c>). One round trip instead of
    /// HasDirectRelation-then-GetIndirectRelations-fan-out.
    /// </summary>
    Task<bool> HasUsersetJoinRelation(string entityType, string entityId, string relation,
        string subEntityType, string computedRelation, string subjectType, string subjectId,
        SnapToken snapToken, CancellationToken cancellationToken);
}
