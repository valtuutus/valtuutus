namespace Valtuutus.Data.Db;

/// <summary>
/// Keys for <see cref="RelationalBatchProviderBase"/>'s dialect catalog (its
/// <c>GetSql</c> hook) — one per batchable query shape. Members whose query takes a
/// name/id array may contain a <c>{0}</c> placeholder in their SQL where the fragment returned by
/// <c>RelationalBatchProviderBase.WriteNameArrayParam</c> lands (an expanded parameter list on
/// providers without a list parameter; Postgres and SqlServer reference their list parameter by
/// name instead).
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
    /// Batched sibling of <see cref="IRelationalCheckOps.HasUsersetJoinRelation"/>: the userset
    /// 2-hop join fast path (SELECT direct-membership-EXISTS OR 2-hop-join-EXISTS).
    /// </summary>
    HasUsersetJoinRelation,

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

    /// <summary>
    /// Batched sibling of <see cref="Valtuutus.Core.Data.IDataReaderProvider.GetAttributesSingleEntity"/>
    /// with a non-null entity id. <c>{0}</c> = attribute-name array.
    /// </summary>
    GetAttributesSingleEntity,
}
