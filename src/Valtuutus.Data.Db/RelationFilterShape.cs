using Valtuutus.Core.Data;

namespace Valtuutus.Data.Db;

/// <summary>
/// The fields a <see cref="DeleteRelationsFilter"/> sets, as a bit mask over <see cref="Columns"/>.
/// Relational providers build one delete statement per shape, comparing only the set fields, so
/// every comparison is a plain equality that an index can serve.
/// </summary>
public static class RelationFilterShape
{
    /// <summary>The relation_tuples column of each mask bit, lowest bit first.</summary>
    public static readonly string[] Columns =
        ["entity_type", "entity_id", "relation", "subject_type", "subject_id", "subject_relation"];

    /// <summary>The number of distinct shapes.</summary>
    public const int Count = 1 << 6;

    /// <summary>
    /// The shape of <paramref name="filter"/>. A field is set when it is non-null; an empty string is
    /// a value, so <c>SubjectRelation = ""</c> matches only direct tuples.
    /// </summary>
    public static int Of(in DeleteRelationsFilter filter) =>
        (filter.EntityType is not null ? 1 << 0 : 0)
        | (filter.EntityId is not null ? 1 << 1 : 0)
        | (filter.Relation is not null ? 1 << 2 : 0)
        | (filter.SubjectType is not null ? 1 << 3 : 0)
        | (filter.SubjectId is not null ? 1 << 4 : 0)
        | (filter.SubjectRelation is not null ? 1 << 5 : 0);

    /// <summary>The columns <paramref name="shape"/> sets, in mask order.</summary>
    public static IEnumerable<string> SetColumns(int shape)
    {
        for (var bit = 0; bit < Columns.Length; bit++)
            if ((shape & (1 << bit)) != 0)
                yield return Columns[bit];
    }
}
