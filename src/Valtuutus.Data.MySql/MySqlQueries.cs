using System.Collections.Concurrent;
using Valtuutus.Core.Data;
using Valtuutus.Data.Db;
using static Valtuutus.Data.MySql.MySqlSql;

namespace Valtuutus.Data.MySql;

/// <summary>
/// The reader's statements for one set of tables and one server type, built and compiled to
/// positional form once. Statements that take an id or name list come in two forms: <c>One</c>
/// compares a single bound value with <c>=</c>, <c>Many</c> joins the values from a JSON array. The
/// caller picks by list length.
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

    public required MySqlStatement GetLatestSnapToken { get; init; }

    // Sets the batch-resolved session variable to the latest token, then returns it. The SET
    // produces no result set, so the command yields exactly one: one row, the token.
    public required MySqlStatement ResolveLatestSnapTokenForBatch { get; init; }
    public required MySqlStatement HasDirectRelation { get; init; }
    public required MySqlStatement HasAnyDirectRelationMany { get; init; }
    public required MySqlStatement HasAnyOfDirectRelationsOne { get; init; }
    public required MySqlStatement HasAnyOfDirectRelationsMany { get; init; }
    public required MySqlStatement HasAnyOfAttributesOne { get; init; }
    public required MySqlStatement HasAnyOfAttributesMany { get; init; }
    public required MySqlStatement HasTrueBoolAttribute { get; init; }
    public required MySqlStatement GetIndirectRelations { get; init; }
    public required MySqlStatement SelectRelationsByTupleFilterNoSubject { get; init; }
    public required MySqlStatement SelectRelationsByTupleFilter { get; init; }
    public required MySqlStatement HasTupleToUserSetRelation { get; init; }
    public required MySqlStatement HasUsersetJoinRelation { get; init; }
    public required MySqlStatement RelationsWithSubjectOne { get; init; }
    public required MySqlStatement RelationsWithSubjectMany { get; init; }
    public required MySqlStatement RelationsWithSubjectOneScoped { get; init; }
    public required MySqlStatement RelationsWithSubjectManyScoped { get; init; }
    public required MySqlStatement RelationsWithSubjectOneMultiRelation { get; init; }
    public required MySqlStatement RelationsWithSubjectManyMultiRelation { get; init; }
    public required MySqlStatement RelationsWithSubjectOneMultiRelationScoped { get; init; }
    public required MySqlStatement RelationsWithSubjectManyMultiRelationScoped { get; init; }
    public required MySqlStatement RelationsWithEntityOne { get; init; }
    public required MySqlStatement RelationsWithEntityMany { get; init; }
    public required MySqlStatement RelationsWithEntityOneMultiRelation { get; init; }
    public required MySqlStatement RelationsWithEntityManyMultiRelation { get; init; }
    public required MySqlStatement Joined { get; init; }
    public required MySqlStatement JoinedScoped { get; init; }
    public required MySqlStatement JoinedByEntityIdsOne { get; init; }
    public required MySqlStatement JoinedByEntityIdsMany { get; init; }
    public required MySqlStatement AttributesByAttribute { get; init; }
    public required MySqlStatement AttributesSingleEntityOne { get; init; }
    public required MySqlStatement AttributesSingleEntityMany { get; init; }
    public required MySqlStatement AttributesByAttributesOne { get; init; }
    public required MySqlStatement AttributesByAttributesMany { get; init; }
    public required MySqlStatement AttributesWithEntityIdsOne { get; init; }
    public required MySqlStatement AttributesWithEntityIdsMany { get; init; }
    public required MySqlStatement AttributesWithEntityIdsMultiAttributeOne { get; init; }
    public required MySqlStatement AttributesWithEntityIdsMultiAttributeMany { get; init; }
    public required MySqlStatement AttributesScopedOne { get; init; }
    public required MySqlStatement AttributesScopedMany { get; init; }
    public required MySqlStatement EntityIdsExcluding { get; init; }
    public required MySqlStatement SubjectIdsExcluding { get; init; }

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

    // Compacts a named template and compiles it to positional form.
    internal static MySqlStatement Statement(string namedSql) => MySqlStatement.Compile(Compact(namedSql));

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

            GetLatestSnapToken = Statement($"SELECT id FROM {tx} ORDER BY id DESC LIMIT 1"),
            ResolveLatestSnapTokenForBatch = Statement($"""
                SET {BatchResolvedSnapToken} = COALESCE((SELECT id FROM {tx} ORDER BY id DESC LIMIT 1), '{SnapToken.MinValue.Value}');
                SELECT {BatchResolvedSnapToken}
                """),
            HasDirectRelation = Statement($"SELECT {directExists}"),
            HasAnyDirectRelationMany = Statement($"""
                SELECT EXISTS(SELECT 1 FROM {JsonIds("@entity_ids", "j")} JOIN {rt} r ON r.entity_id = j.id
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.relation = @relation
                  AND r.subject_id = @subject_id AND r.subject_relation = '')
                """),
            HasAnyOfDirectRelationsOne = Statement($"SELECT {H("r")}r.relation FROM {rt} r WHERE {directOne} LIMIT 1"),
            HasAnyOfDirectRelationsMany = Statement($"""
                SELECT DISTINCT r.relation FROM {JsonNames("@relations", "j")} JOIN {rt} r ON r.relation = j.name
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id
                  AND r.subject_id = @subject_id AND r.subject_relation = ''
                """),
            HasAnyOfAttributesOne = Statement($"""
                SELECT {H("a")}a.attribute FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id AND a.attribute = @attribute
                  AND a.value = 'true'
                LIMIT 1
                """),
            HasAnyOfAttributesMany = Statement($"""
                SELECT DISTINCT a.attribute FROM {JsonNames("@attributes", "j")} JOIN {at} a ON a.attribute = j.name
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id AND a.value = 'true'
                """),
            HasTrueBoolAttribute = Statement($"""
                SELECT EXISTS(SELECT {H("a")}1 FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id AND a.attribute = @attribute
                  AND a.value = 'true')
                """),
            GetIndirectRelations = Statement($"""
                SELECT {ColsOf("r")} FROM {rt} r
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id AND r.relation = @relation
                  AND r.subject_relation <> ''
                """),
            SelectRelationsByTupleFilterNoSubject = Statement($"""
                SELECT {H("r")}{ColsOf("r")} FROM {rt} r
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id AND r.relation = @relation
                """),
            SelectRelationsByTupleFilter = Statement($"""
                SELECT {ColsOf("r")} FROM {rt} r
                WHERE {P("r")} AND r.entity_type = @entity_type AND r.entity_id = @entity_id AND r.relation = @relation
                  AND (@subject_id IS NULL OR r.subject_id = @subject_id)
                  AND (@subject_relation IS NULL OR r.subject_relation = @subject_relation)
                  AND (@subject_type IS NULL OR r.subject_type = @subject_type)
                """),
            HasTupleToUserSetRelation = Statement(
                $"SELECT {TwoHop(rt, snap, "@tuple_set_relation", "@sub_entity_type", "''", "@computed_relation")}"),
            HasUsersetJoinRelation = Statement(
                $"SELECT {directExists} OR {TwoHop(rt, snap, "@relation", "@sub_entity_type", "@computed_relation", "@computed_relation")}"),

            RelationsWithSubjectOne = Statement($"""
                SELECT {H("r")}{ColsOf("r")} FROM {rt} r
                WHERE {withSubjectFilter} AND r.relation = @relation AND r.subject_id = @subject_id
                """),
            RelationsWithSubjectMany = Statement($"""
                SELECT {ColsOf("r")} FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                WHERE {withSubjectFilter} AND r.relation = @relation
                """),
            RelationsWithSubjectOneScoped = Statement($"""
                SELECT DISTINCT {ColsOf("r")} FROM {rt} r JOIN {rt} sc ON sc.entity_id = r.entity_id
                WHERE {withSubjectFilter} AND r.relation = @relation AND r.subject_id = @subject_id AND {scope}
                """),
            RelationsWithSubjectManyScoped = Statement($"""
                SELECT DISTINCT {ColsOf("r")} FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                JOIN {rt} sc ON sc.entity_id = r.entity_id
                WHERE {withSubjectFilter} AND r.relation = @relation AND {scope}
                """),
            RelationsWithSubjectOneMultiRelation = Statement($"""
                SELECT {ColsOf("r")} FROM {rt} r JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                WHERE {withSubjectFilter} AND r.subject_id = @subject_id
                """),
            RelationsWithSubjectManyMultiRelation = Statement($"""
                SELECT {ColsOf("r")} FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                WHERE {withSubjectFilter}
                """),
            RelationsWithSubjectOneMultiRelationScoped = Statement($"""
                SELECT DISTINCT {ColsOf("r")} FROM {rt} r JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                JOIN {rt} sc ON sc.entity_id = r.entity_id
                WHERE {withSubjectFilter} AND r.subject_id = @subject_id AND {scope}
                """),
            RelationsWithSubjectManyMultiRelationScoped = Statement($"""
                SELECT DISTINCT {ColsOf("r")} FROM {JsonIds("@subject_ids", "j")} JOIN {rt} r ON r.subject_id = j.id
                JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                JOIN {rt} sc ON sc.entity_id = r.entity_id
                WHERE {withSubjectFilter} AND {scope}
                """),

            RelationsWithEntityOne = Statement($"""
                SELECT {H("r")}{ColsOf("r")} FROM {rt} r
                WHERE {withEntityFilter} AND r.relation = @relation AND r.entity_id = @entity_id
                """),
            RelationsWithEntityMany = Statement($"""
                SELECT {ColsOf("r")} FROM {JsonIds("@entity_ids", "j")} JOIN {rt} r ON r.entity_id = j.id
                WHERE {withEntityFilter} AND r.relation = @relation
                """),
            RelationsWithEntityOneMultiRelation = Statement($"""
                SELECT {ColsOf("r")} FROM {rt} r JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                WHERE {withEntityFilter} AND r.entity_id = @entity_id
                """),
            RelationsWithEntityManyMultiRelation = Statement($"""
                SELECT {ColsOf("r")} FROM {JsonIds("@entity_ids", "j")} JOIN {rt} r ON r.entity_id = j.id
                JOIN {JsonNames("@relations", "n")} ON r.relation = n.name
                WHERE {withEntityFilter}
                """),

            Joined = Statement($"""
                SELECT DISTINCT {ColsOf("m")} FROM {rt} d JOIN {rt} m ON m.subject_id = d.entity_id
                WHERE {joinedFilter}
                """),
            JoinedScoped = Statement($"""
                SELECT DISTINCT {ColsOf("m")} FROM {rt} d JOIN {rt} m ON m.subject_id = d.entity_id
                JOIN {rt} sc ON sc.entity_id = m.entity_id
                WHERE {joinedFilter} AND {scope}
                """),
            JoinedByEntityIdsOne = Statement($"""
                SELECT DISTINCT {ColsOf("d")} FROM {rt} m JOIN {rt} d ON d.entity_id = m.subject_id
                WHERE {P("m")} AND m.entity_type = @entity_type AND m.entity_id = @entity_id AND {joinedByEntityIdsFilter}
                """),
            JoinedByEntityIdsMany = Statement($"""
                SELECT DISTINCT {ColsOf("d")} FROM {JsonIds("@entity_ids", "j")} JOIN {rt} m ON m.entity_id = j.id
                JOIN {rt} d ON d.entity_id = m.subject_id
                WHERE {P("m")} AND m.entity_type = @entity_type AND {joinedByEntityIdsFilter}
                """),

            AttributesByAttribute = Statement($"""
                SELECT {AttrColsOf("a")} FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute
                  AND (@entity_id IS NULL OR a.entity_id = @entity_id)
                """),
            AttributesSingleEntityOne = Statement($"""
                SELECT {H("a")}{AttrColsOf("a")} FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id AND a.attribute = @attribute
                """),
            AttributesSingleEntityMany = Statement($"""
                SELECT {AttrColsOf("a")} FROM {JsonNames("@attributes", "n")} JOIN {at} a ON a.attribute = n.name
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id
                """),
            AttributesByAttributesOne = Statement($"""
                SELECT {AttrColsOf("a")} FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute
                  AND (@entity_id IS NULL OR a.entity_id = @entity_id)
                """),
            AttributesByAttributesMany = Statement($"""
                SELECT {AttrColsOf("a")} FROM {JsonNames("@attributes", "n")} JOIN {at} a ON a.attribute = n.name
                WHERE {P("a")} AND a.entity_type = @entity_type AND (@entity_id IS NULL OR a.entity_id = @entity_id)
                """),
            AttributesWithEntityIdsOne = Statement($"""
                SELECT {H("a")}{AttrColsOf("a")} FROM {at} a
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute AND a.entity_id = @entity_id
                """),
            AttributesWithEntityIdsMany = Statement($"""
                SELECT {AttrColsOf("a")} FROM {JsonIds("@entity_ids", "j")} JOIN {at} a ON a.entity_id = j.id
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute
                """),
            AttributesWithEntityIdsMultiAttributeOne = Statement($"""
                SELECT {AttrColsOf("a")} FROM {JsonNames("@attributes", "n")} JOIN {at} a ON a.attribute = n.name
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.entity_id = @entity_id
                """),
            AttributesWithEntityIdsMultiAttributeMany = Statement($"""
                SELECT {AttrColsOf("a")} FROM {JsonIds("@entity_ids", "j")} JOIN {at} a ON a.entity_id = j.id
                JOIN {JsonNames("@attributes", "n")} ON a.attribute = n.name
                WHERE {P("a")} AND a.entity_type = @entity_type
                """),
            AttributesScopedOne = Statement($"""
                SELECT DISTINCT {AttrColsOf("a")} FROM {at} a JOIN {rt} sc ON sc.entity_id = a.entity_id
                WHERE {P("a")} AND a.entity_type = @entity_type AND a.attribute = @attribute AND {scope}
                """),
            AttributesScopedMany = Statement($"""
                SELECT DISTINCT {AttrColsOf("a")} FROM {at} a JOIN {JsonNames("@attributes", "n")} ON a.attribute = n.name
                JOIN {rt} sc ON sc.entity_id = a.entity_id
                WHERE {P("a")} AND a.entity_type = @entity_type AND {scope}
                """),

            EntityIdsExcluding = Statement($"""
                SELECT DISTINCT u.entity_id FROM (
                    SELECT r.entity_id FROM {rt} r WHERE {P("r")} AND r.entity_type = @entity_type
                    UNION
                    SELECT a.entity_id FROM {at} a WHERE {P("a")} AND a.entity_type = @entity_type
                ) u
                WHERE NOT EXISTS (SELECT 1 FROM {JsonIds("@exclude_ids", "x")} WHERE x.id = u.entity_id)
                """),
            SubjectIdsExcluding = Statement($"""
                SELECT DISTINCT r.subject_id FROM {rt} r
                WHERE {P("r")} AND r.subject_type = @subject_type AND r.subject_relation = ''
                  AND NOT EXISTS (SELECT 1 FROM {JsonIds("@exclude_ids", "x")} WHERE x.id = r.subject_id)
                """),
        };
    }
}
