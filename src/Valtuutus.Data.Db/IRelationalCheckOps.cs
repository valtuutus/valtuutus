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
}
