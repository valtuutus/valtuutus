using System.Collections.Concurrent;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

/// <summary>
/// The reader's SQL text for one set of tables and one server type, built once. Statements that
/// take an id or name list come in two forms: <c>One</c> compares a single bound value with
/// <c>=</c>, <c>Many</c> joins the values from a JSON array. The caller picks by list length.
/// </summary>
internal sealed record MySqlQueries
{
    private static readonly ConcurrentDictionary<(DbQueryCacheKey Tables, MySqlServerType ServerType, string Snap), MySqlQueries> Cache = new();

    internal const string SnapTokenParameter = "@snap_token";

    // The session variable the batch-resolved snap token is parked in. Only a batch whose first
    // command is ResolveLatestSnapTokenForBatch may read it: pooled connections keep their session
    // state, so anywhere else it holds whatever an earlier batch left there.
    internal const string BatchResolvedSnapToken = "@valtuutus_snap";

    internal static MySqlQueries Get(ValtuutusMySqlOptions o) => Get(o, SnapTokenParameter);

    // The same statements reading the token from the batch-resolved session variable instead of a
    // bound parameter.
    internal static MySqlQueries GetBatchResolved(ValtuutusMySqlOptions o) => Get(o, BatchResolvedSnapToken);

    private static MySqlQueries Get(ValtuutusMySqlOptions o, string snap) =>
        Cache.GetOrAdd((DbQueryCacheKey.From(o), o.ServerType, snap), static (key, opts) => Build(opts, key.Snap), o);

    public required MySqlServerType ServerType { get; init; }
    public required string Snap { get; init; }
    public required string RelationsTable { get; init; }
    public required string AttributesTable { get; init; }

    public required string GetLatestSnapToken { get; init; }

    // Sets the batch-resolved session variable to the latest token, then returns it. The SET
    // produces no result set, so the command yields exactly one: one row, the token.
    public required string ResolveLatestSnapTokenForBatch { get; init; }
    public required string HasDirectRelation { get; init; }
    public required string HasAnyDirectRelationMany { get; init; }
    public required string HasAnyOfDirectRelationsOne { get; init; }
    public required string HasAnyOfDirectRelationsMany { get; init; }
    public required string HasAnyOfAttributesOne { get; init; }
    public required string HasAnyOfAttributesMany { get; init; }
    public required string HasTrueBoolAttribute { get; init; }
    public required string GetIndirectRelations { get; init; }
    public required string SelectRelationsByTupleFilterNoSubject { get; init; }
    public required string SelectRelationsByTupleFilter { get; init; }
    public required string HasTupleToUserSetRelation { get; init; }
    public required string HasUsersetJoinRelation { get; init; }
    public required string RelationsWithSubjectOne { get; init; }
    public required string RelationsWithSubjectMany { get; init; }
    public required string RelationsWithSubjectOneScoped { get; init; }
    public required string RelationsWithSubjectManyScoped { get; init; }
    public required string RelationsWithSubjectOneMultiRelation { get; init; }
    public required string RelationsWithSubjectManyMultiRelation { get; init; }
    public required string RelationsWithSubjectOneMultiRelationScoped { get; init; }
    public required string RelationsWithSubjectManyMultiRelationScoped { get; init; }
    public required string RelationsWithEntityOne { get; init; }
    public required string RelationsWithEntityMany { get; init; }
    public required string RelationsWithEntityOneMultiRelation { get; init; }
    public required string RelationsWithEntityManyMultiRelation { get; init; }
    public required string Joined { get; init; }
    public required string JoinedScoped { get; init; }
    public required string JoinedByEntityIdsOne { get; init; }
    public required string JoinedByEntityIdsMany { get; init; }
    public required string AttributesByAttribute { get; init; }
    public required string AttributesSingleEntityOne { get; init; }
    public required string AttributesSingleEntityMany { get; init; }
    public required string AttributesByAttributesOne { get; init; }
    public required string AttributesByAttributesMany { get; init; }
    public required string AttributesWithEntityIdsOne { get; init; }
    public required string AttributesWithEntityIdsMany { get; init; }
    public required string AttributesWithEntityIdsMultiAttributeOne { get; init; }
    public required string AttributesWithEntityIdsMultiAttributeMany { get; init; }
    public required string AttributesScopedOne { get; init; }
    public required string AttributesScopedMany { get; init; }
    public required string EntityIdsExcluding { get; init; }
    public required string SubjectIdsExcluding { get; init; }

    // Does the subject hold dep on some sub-typed entity that the checked entity relates to through
    // rel (with msr as the tuple's subject relation)? The optimizer picks the join order per
    // execution from the index statistics of both sides. The suffix keeps the aliases of several
    // fragments in one statement apart.
    internal static string TwoHop(string relationsTable, string snap, string rel, string sub, string msr, string dep,
        string suffix = "")
    {
        var m = $"m{suffix}";
        var d = $"d{suffix}";
        return $"""
            EXISTS(SELECT 1 FROM {relationsTable} {m} JOIN {relationsTable} {d} ON {d}.entity_id = {m}.subject_id
            WHERE {MySqlSql.Snap(m, snap)} AND {m}.entity_type = @entity_type AND {m}.entity_id = @entity_id
              AND {m}.relation = {rel} AND {m}.subject_type = {sub} AND {m}.subject_relation = {msr}
              AND {MySqlSql.Snap(d, snap)} AND {d}.entity_type = {sub} AND {d}.relation = {dep}
              AND {d}.subject_type = @subject_type AND {d}.subject_id = @subject_id AND {d}.subject_relation = '')
            """;
    }

    internal static MySqlQueries Build(ValtuutusMySqlOptions o, string snap)
    {
        var rt = MySqlConnections.Relations(o);
        var at = MySqlConnections.Attributes(o);
        var tx = MySqlConnections.Transactions(o);
        string P(string alias) => MySqlSql.Snap(alias, snap);
        string H(string alias) => PointHint(o.ServerType, alias);

        var scope = $"""
            {P("sc")} AND sc.entity_type = @entity_type AND sc.relation = @scope_relation
              AND sc.subject_type = @scope_subject_type AND sc.subject_id = @scope_subject_id AND sc.subject_relation = ''
            """;
        var withSubjectFilter = $"""
            {P("r")} AND r.entity_type = @entity_type AND r.subject_type = @subject_type AND r.subject_relation = @subject_relation
            """;
        var withEntityFilter = $"""
            {P("r")} AND r.subject_type = @subject_type AND r.entity_type = @entity_type AND r.subject_relation = @subject_relation
            """;
        var joinedFilter = $"""
            {P("d")} AND d.entity_type = @sub_entity_type AND d.relation = @sub_relation AND d.subject_type = @subject_type
              AND d.subject_id = @subject_id AND d.subject_relation = ''
              AND {P("m")} AND m.entity_type = @entity_type AND m.relation = @relation AND m.subject_type = @sub_entity_type
              AND m.subject_relation = @subject_relation
            """;
        var joinedByEntityIdsFilter = $"""
            m.relation = @relation AND m.subject_type = @sub_entity_type AND m.subject_relation = @subject_relation
              AND {P("d")} AND d.entity_type = @sub_entity_type AND d.relation = @sub_relation AND d.subject_type = @subject_type
              AND d.subject_relation = ''
            """;
        var directOne = $"""
            {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id AND r.relation = @relation
              AND r.subject_id = @subject_id AND r.subject_relation = ''
            """;
        var directExists = $"EXISTS(SELECT {H("r")}1 FROM {rt} r WHERE {directOne})";

        return new MySqlQueries
        {
            ServerType = o.ServerType,
            Snap = snap,
            RelationsTable = rt,
            AttributesTable = at,

            GetLatestSnapToken = Compact($"SELECT id FROM {tx} ORDER BY id DESC LIMIT 1"),
            ResolveLatestSnapTokenForBatch = Compact($"""
                SET {BatchResolvedSnapToken} = COALESCE((SELECT id FROM {tx} ORDER BY id DESC LIMIT 1), '{SnapToken.MinValue.Value}');
                SELECT {BatchResolvedSnapToken}
                """),
            HasDirectRelation = Compact($"SELECT {directExists}"),
            HasAnyDirectRelationMany = Compact($"""
                SELECT EXISTS(SELECT 1 FROM {JsonIds("@entity_ids", "j")} JOIN {rt} r ON r.entity_id = j.id
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.relation = @relation
                  AND r.subject_id = @subject_id AND r.subject_relation = '')
                """),
            HasAnyOfDirectRelationsOne = Compact($"SELECT {H("r")}r.relation FROM {rt} r WHERE {directOne} LIMIT 1"),
            HasAnyOfDirectRelationsMany = Compact($"""
                SELECT DISTINCT r.relation FROM {JsonNames("@relations", "j")} JOIN {rt} r ON r.relation = j.name
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id
                  AND r.subject_id = @subject_id AND r.subject_relation = ''
                """),
            HasAnyOfAttributesOne = Compact($"""
                SELECT {H("a")}a.attribute FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id AND a.attribute = @attribute
                  AND a.value = 'true'
                LIMIT 1
                """),
            HasAnyOfAttributesMany = Compact($"""
                SELECT DISTINCT a.attribute FROM {JsonNames("@attributes", "j")} JOIN {at} a ON a.attribute = j.name
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id AND a.value = 'true'
                """),
            HasTrueBoolAttribute = Compact($"""
                SELECT EXISTS(SELECT {H("a")}1 FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id AND a.attribute = @attribute
                  AND a.value = 'true')
                """),
            GetIndirectRelations = Compact($"""
                SELECT {ColsOf("r")} FROM {rt} r
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id AND r.relation = @relation
                  AND r.subject_relation <> ''
                """),
            SelectRelationsByTupleFilterNoSubject = Compact($"""
                SELECT {H("r")}{ColsOf("r")} FROM {rt} r
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id AND r.relation = @relation
                """),
            SelectRelationsByTupleFilter = Compact($"""
                SELECT {ColsOf("r")} FROM {rt} r
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id AND r.relation = @relation
                  AND (@subject_id IS NULL OR r.subject_id = @subject_id)
                  AND (@subject_relation IS NULL OR r.subject_relation = @subject_relation)
                  AND (@subject_type IS NULL OR r.subject_type = @subject_type)
                """),
            HasTupleToUserSetRelation = Compact(
                $"SELECT {TwoHop(rt, snap, "@tuple_set_relation", "@sub_entity_type", "''", "@computed_relation")}"),
            HasUsersetJoinRelation = Compact(
                $"SELECT {directExists} OR {TwoHop(rt, snap, "@relation", "@sub_entity_type", "@computed_relation", "@computed_relation")}"),

            RelationsWithSubjectOne = Compact($"""
                SELECT {H("r")}{ColsOf("r")} FROM {rt} r
                WHERE {withSubjectFilter} AND r.relation = @relation AND r.subject_id = @subject_id
                """),
            RelationsWithSubjectMany = Compact($"""
                SELECT {ColsOf("r")} FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                WHERE {withSubjectFilter} AND r.relation = @relation
                """),
            RelationsWithSubjectOneScoped = Compact($"""
                SELECT DISTINCT {ColsOf("r")} FROM {rt} r JOIN {rt} sc ON sc.entity_id = r.entity_id
                WHERE {withSubjectFilter} AND r.relation = @relation AND r.subject_id = @subject_id AND {scope}
                """),
            RelationsWithSubjectManyScoped = Compact($"""
                SELECT DISTINCT {ColsOf("r")} FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                JOIN {rt} sc ON sc.entity_id = r.entity_id
                WHERE {withSubjectFilter} AND r.relation = @relation AND {scope}
                """),
            RelationsWithSubjectOneMultiRelation = Compact($"""
                SELECT {ColsOf("r")} FROM {rt} r JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                WHERE {withSubjectFilter} AND r.subject_id = @subject_id
                """),
            RelationsWithSubjectManyMultiRelation = Compact($"""
                SELECT {ColsOf("r")} FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                WHERE {withSubjectFilter}
                """),
            RelationsWithSubjectOneMultiRelationScoped = Compact($"""
                SELECT DISTINCT {ColsOf("r")} FROM {rt} r JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                JOIN {rt} sc ON sc.entity_id = r.entity_id
                WHERE {withSubjectFilter} AND r.subject_id = @subject_id AND {scope}
                """),
            RelationsWithSubjectManyMultiRelationScoped = Compact($"""
                SELECT DISTINCT {ColsOf("r")} FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                JOIN {rt} sc ON sc.entity_id = r.entity_id
                WHERE {withSubjectFilter} AND {scope}
                """),

            RelationsWithEntityOne = Compact($"""
                SELECT {H("r")}{ColsOf("r")} FROM {rt} r
                WHERE {withEntityFilter} AND r.relation = @relation AND r.entity_id = @entity_id
                """),
            RelationsWithEntityMany = Compact($"""
                SELECT {ColsOf("r")} FROM {JsonIds("@entity_ids", "j")} JOIN {rt} r ON r.entity_id = j.id
                WHERE {withEntityFilter} AND r.relation = @relation
                """),
            RelationsWithEntityOneMultiRelation = Compact($"""
                SELECT {ColsOf("r")} FROM {rt} r JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                WHERE {withEntityFilter} AND r.entity_id = @entity_id
                """),
            RelationsWithEntityManyMultiRelation = Compact($"""
                SELECT {ColsOf("r")} FROM {JsonIds("@entity_ids", "j")} JOIN {rt} r ON r.entity_id = j.id
                JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                WHERE {withEntityFilter}
                """),

            Joined = Compact($"""
                SELECT DISTINCT {ColsOf("m")} FROM {rt} d JOIN {rt} m ON m.subject_id = d.entity_id
                WHERE {joinedFilter}
                """),
            JoinedScoped = Compact($"""
                SELECT DISTINCT {ColsOf("m")} FROM {rt} d JOIN {rt} m ON m.subject_id = d.entity_id
                JOIN {rt} sc ON sc.entity_id = m.entity_id
                WHERE {joinedFilter} AND {scope}
                """),
            JoinedByEntityIdsOne = Compact($"""
                SELECT DISTINCT {ColsOf("d")} FROM {rt} m JOIN {rt} d ON d.entity_id = m.subject_id
                WHERE {P("m")} AND m.entity_type = @entity_type AND m.entity_id = @entity_id AND {joinedByEntityIdsFilter}
                """),
            JoinedByEntityIdsMany = Compact($"""
                SELECT DISTINCT {ColsOf("d")} FROM {JsonIds("@entity_ids", "j")} JOIN {rt} m ON m.entity_id = j.id
                JOIN {rt} d ON d.entity_id = m.subject_id
                WHERE {P("m")} AND m.entity_type = @entity_type AND {joinedByEntityIdsFilter}
                """),

            AttributesByAttribute = Compact($"""
                SELECT {AttrColsOf("a")} FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute
                  AND (@entity_id IS NULL OR a.entity_id = @entity_id)
                """),
            AttributesSingleEntityOne = Compact($"""
                SELECT {H("a")}{AttrColsOf("a")} FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id AND a.attribute = @attribute
                """),
            AttributesSingleEntityMany = Compact($"""
                SELECT {AttrColsOf("a")} FROM {JsonNames("@attributes", "n")} JOIN {at} a ON a.attribute = n.name
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id
                """),
            AttributesByAttributesOne = Compact($"""
                SELECT {AttrColsOf("a")} FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute
                  AND (@entity_id IS NULL OR a.entity_id = @entity_id)
                """),
            AttributesByAttributesMany = Compact($"""
                SELECT {AttrColsOf("a")} FROM {JsonNames("@attributes", "n")} JOIN {at} a ON a.attribute = n.name
                WHERE {P("a")} AND a.entity_type = @entity_type AND (@entity_id IS NULL OR a.entity_id = @entity_id)
                """),
            AttributesWithEntityIdsOne = Compact($"""
                SELECT {H("a")}{AttrColsOf("a")} FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute AND a.entity_id = @entity_id
                """),
            AttributesWithEntityIdsMany = Compact($"""
                SELECT {AttrColsOf("a")} FROM {JsonIds("@entity_ids", "j")} JOIN {at} a ON a.entity_id = j.id
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute
                """),
            AttributesWithEntityIdsMultiAttributeOne = Compact($"""
                SELECT {AttrColsOf("a")} FROM {JsonNames("@attributes", "n")} JOIN {at} a ON a.attribute = n.name
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id
                """),
            AttributesWithEntityIdsMultiAttributeMany = Compact($"""
                SELECT {AttrColsOf("a")} FROM {JsonIds("@entity_ids", "j")} JOIN {at} a ON a.entity_id = j.id
                JOIN {JsonNames("@attributes", "n")} ON a.attribute = n.name
                WHERE {P("a")} AND a.entity_type = @entity_type
                """),
            AttributesScopedOne = Compact($"""
                SELECT DISTINCT {AttrColsOf("a")} FROM {at} a JOIN {rt} sc ON sc.entity_id = a.entity_id
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute AND {scope}
                """),
            AttributesScopedMany = Compact($"""
                SELECT DISTINCT {AttrColsOf("a")} FROM {at} a JOIN {JsonNames("@attributes", "n")} ON a.attribute = n.name
                JOIN {rt} sc ON sc.entity_id = a.entity_id
                WHERE {P("a")} AND a.entity_type = @entity_type AND {scope}
                """),

            EntityIdsExcluding = Compact($"""
                SELECT DISTINCT u.entity_id FROM (
                    SELECT r.entity_id FROM {rt} r WHERE {P("r")} AND r.entity_type = @entity_type
                    UNION
                    SELECT a.entity_id FROM {at} a WHERE {P("a")} AND a.entity_type = @entity_type
                ) u
                WHERE NOT EXISTS (SELECT 1 FROM {JsonIds("@exclude_ids", "x")} WHERE x.id = u.entity_id)
                """),
            SubjectIdsExcluding = Compact($"""
                SELECT DISTINCT r.subject_id FROM {rt} r
                WHERE {P("r")} AND r.subject_type = @subject_type AND r.subject_relation = ''
                  AND NOT EXISTS (SELECT 1 FROM {JsonIds("@exclude_ids", "x")} WHERE x.id = r.subject_id)
                """),
        };
    }
}
