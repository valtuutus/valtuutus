namespace Valtuutus.Data.Db;

/// <summary>
/// Keys for <see cref="RelationalBatchProviderBase"/>'s dialect catalog (its
/// <c>GetSql</c> hook) — one per batchable query shape. Members whose query takes a
/// name/id array contain a <c>{0}</c> placeholder in their SQL where the fragment returned by
/// <c>RelationalBatchProviderBase.WriteNameArrayParam</c> lands (a native array parameter
/// reference on Postgres; on SqlServer the SQL text hardcodes a table-valued-parameter subquery
/// instead, so the placeholder is a no-op there; an expanded parameter list on providers with
/// neither).
/// </summary>
public enum RelationalBatchQuery
{
    /// <summary>Batched sibling of <see cref="Valtuutus.Core.Data.IDataReaderProvider.HasDirectRelation"/>.</summary>
    HasDirectRelation,

    /// <summary>Batched sibling of <see cref="Valtuutus.Core.Data.IDataReaderProvider.HasTrueBoolAttribute"/>.</summary>
    HasTrueBoolAttribute,

    /// <summary>Batched sibling of <see cref="Valtuutus.Core.Data.IDataReaderProvider.HasTupleToUserSetRelation"/>.</summary>
    HasTupleToUserSetRelation,

    /// <summary>
    /// Batched sibling of <see cref="Valtuutus.Core.Data.IDataReaderProvider.HasAnyDirectRelation"/>.
    /// <c>{0}</c> = entity-id array.
    /// </summary>
    HasAnyDirectRelation,

    /// <summary>
    /// Batched sibling of <see cref="IRelationalCheckOps.HasAnyOfDirectRelations"/>.
    /// <c>{0}</c> = relation-name array.
    /// </summary>
    HasAnyOfDirectRelations,

    /// <summary>
    /// Batched sibling of <see cref="IRelationalCheckOps.HasAnyOfAttributes"/>.
    /// <c>{0}</c> = attribute-name array.
    /// </summary>
    HasAnyOfAttributes,

    /// <summary>
    /// Batched sibling of <see cref="Valtuutus.Core.Data.IDataReaderProvider.GetRelations"/> when the
    /// filter carries no subject filters — the same SQL-shape branch the non-batch method takes.
    /// </summary>
    GetRelations,

    /// <summary>
    /// Batched sibling of <see cref="Valtuutus.Core.Data.IDataReaderProvider.GetRelations"/> when the
    /// filter carries at least one subject filter (subject id/relation/type predicates present).
    /// </summary>
    GetRelationsWithSubjectFilters,

    /// <summary>Batched sibling of <see cref="Valtuutus.Core.Data.IDataReaderProvider.GetIndirectRelations"/>.</summary>
    GetIndirectRelations,
}
