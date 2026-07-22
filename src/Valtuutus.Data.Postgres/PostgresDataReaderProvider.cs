using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Valtuutus.Data.Db;

namespace Valtuutus.Data.Postgres;

public class PostgresDataReaderProvider : RateLimiterExecuter, IDataReaderProvider, IRelationalCheckOps
{
    private const string UnformattedSelectAttributes = @"SELECT
                    entity_type,
                    entity_id,
                    attribute,
                    value
                FROM {0}.{1} /**where**/";

    private const string UnformattedSelectRelations = @"SELECT
                    entity_type,
                    entity_id,
                    relation,
                    subject_type,
                    subject_id,
                    subject_relation
                FROM {0}.{1} /**where**/";

    private const string UnformattedExistsRelation = "SELECT EXISTS(SELECT 1 FROM {0}.{1} /**where**/)";
    private const string SnapTokenPredicate = "created_tx_id <= @snap_token AND (deleted_tx_id IS NULL OR deleted_tx_id > @snap_token)";

    // Internal (not private): PostgresBatchOps' dialect catalog serves the exact same SQL text as
    // these single-op queries — one definition, shared through GetQueries, so the two paths can
    // never drift apart (and the server's prepared-statement cache sees one statement, not two).
    internal sealed record ReaderQueries
    {
        public required string SelectAttributes { get; init; }
        public required string SelectRelations { get; init; }
        public required string GetLatestSnapToken { get; init; }
        public required string Select1Attribute { get; init; }
        public required string ExistsRelation { get; init; }
        public required string HasDirectRelation { get; init; }
        public required string HasAnyDirectRelation { get; init; }
        public required string HasAnyOfDirectRelations { get; init; }
        public required string HasAnyOfAttributes { get; init; }
        // {0}-placeholder templates of the three array-taking queries above, for PostgresBatchOps'
        // dialect catalog (RelationalBatchProviderBase substitutes WriteNameArrayParam's fragment).
        // The non-template properties are string.Format-derived from these in BuildQueries, so the
        // batched and single-op SQL text stay byte-identical by construction.
        public required string HasAnyDirectRelationBatchTemplate { get; init; }
        public required string HasAnyOfDirectRelationsBatchTemplate { get; init; }
        public required string HasAnyOfAttributesBatchTemplate { get; init; }
        public required string GetIndirectRelations { get; init; }
        public required string GetRelationsWithSingleSubjectSnap { get; init; }
        public required string GetRelationsWithMultiSubjectSnap { get; init; }
        public required string GetRelationsWithSingleSubjectMultiRelationSnap { get; init; }
        public required string GetRelationsWithMultiSubjectMultiRelationSnap { get; init; }
        public required string GetRelationsWithEntityIdsMultiRelationSnap { get; init; }
        public required string GetRelationsJoined { get; init; }
        public required string GetRelationsJoinedByEntityIds { get; init; }
        public required string HasTupleToUserSetRelation { get; init; }
        public required string HasUsersetJoinRelation { get; init; }
        public required string GetAttribute { get; init; }
        public required string GetAttributeWithEntityId { get; init; }
        public required string HasTrueBoolAttribute { get; init; }
        public required string GetRelationsWithSingleSubjectScoped { get; init; }
        public required string GetRelationsWithMultiSubjectScoped { get; init; }
        public required string GetRelationsWithSingleSubjectMultiRelationScoped { get; init; }
        public required string GetRelationsWithMultiSubjectMultiRelationScoped { get; init; }
        public required string GetRelationsJoinedScoped { get; init; }
        public required string GetAttributesDictScoped { get; init; }
        public required string GetEntityIdsExcluding { get; init; }
        public required string GetSubjectIdsExcluding { get; init; }
        public required string SelectRelationsByTupleFilter { get; init; }
        public required string SelectRelationsByTupleFilterNoSubject { get; init; }
        public required string SelectRelationsWithEntityIds { get; init; }
        public required string SelectAttributesByEntityAttributeFilter { get; init; }
        public required string SelectAttributesByEntityAttributesFilter { get; init; }
        public required string SelectAttributesWithEntityIdsByAttributeFilter { get; init; }
        public required string SelectAttributesWithEntityIdsByEntityAttributesFilter { get; init; }
    }

    private static readonly ConcurrentDictionary<DbQueryCacheKey, ReaderQueries> QueryCache = new();
    private readonly ReaderQueries _q;

    private readonly DbConnectionFactory _connectionFactory;
    private readonly NpgsqlDataSource _hotPathDataSource;
    private static readonly ConcurrentDictionary<DataSourceCacheKey, NpgsqlDataSource> DataSourceCache = new();
    private readonly record struct DataSourceCacheKey(string ConnectionString, int MaxAutoPrepare, int AutoPrepareMinUsages);

    // The three parameter helpers below are internal (not private) for the same reason as
    // ReaderQueries: PostgresBatchOps' parameter hooks construct byte-identical NpgsqlParameters
    // (same NpgsqlDbType, same Size) through these exact helpers instead of re-deriving them.
    internal static void AddStringParameter(NpgsqlParameterCollection parameters, string name, string value, int size)
    {
        parameters.Add(new NpgsqlParameter<string>(name, NpgsqlDbType.Varchar)
        {
            Size = size,
            TypedValue = value
        });
    }

    internal static void AddFixedCharParameter(NpgsqlParameterCollection parameters, string name, string value, int size)
    {
        parameters.Add(new NpgsqlParameter<string>(name, NpgsqlDbType.Char)
        {
            Size = size,
            TypedValue = value
        });
    }

    internal static void AddNullableStringParameter(NpgsqlParameterCollection parameters, string name, string? value, int size)
    {
        parameters.Add(new NpgsqlParameter<string?>(name, NpgsqlDbType.Varchar)
        {
            Size = size,
            TypedValue = string.IsNullOrWhiteSpace(value) ? null : value
        });
    }

    private static RelationTuple ReadRelationTuple(NpgsqlDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5));

    private static JsonValue ParseJsonValue(string rawJson)
    {
        return JsonNode.Parse(rawJson) as JsonValue
            ?? throw new InvalidOperationException("Attribute value must deserialize to a JsonValue.");
    }

    private static AttributeTuple ReadAttributeTuple(NpgsqlDataReader reader)
    {
        return new AttributeTuple(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseJsonValue(reader.GetString(3)));
    }

    // Internal so PostgresBatchOps binds its batches to the SAME cached NpgsqlDataSource (same
    // cache key, same instance) the reader uses — one connection pool and one auto-prepare cache
    // for both the single-op and the batched path.
    internal static NpgsqlDataSource GetOrCreateDataSource(string connectionString, ValtuutusPostgresOptions options)
    {
        var key = new DataSourceCacheKey(connectionString, options.MaxAutoPrepare, options.AutoPrepareMinUsages);
        return DataSourceCache.GetOrAdd(key, static cacheKey =>
        {
            var csb = new NpgsqlConnectionStringBuilder(cacheKey.ConnectionString);
            if (cacheKey.MaxAutoPrepare > 0)
            {
                csb.MaxAutoPrepare = cacheKey.MaxAutoPrepare;
                csb.AutoPrepareMinUsages = cacheKey.AutoPrepareMinUsages;
            }

            return NpgsqlDataSource.Create(csb.ConnectionString);
        });
    }

    public PostgresDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        ValtuutusPostgresOptions dbOptions) : base(options)
    {
        _connectionFactory = connectionFactory;
        _q = GetQueries(dbOptions);
        using var probeConnection = (NpgsqlConnection)_connectionFactory();
        _hotPathDataSource = GetOrCreateDataSource(probeConnection.ConnectionString, dbOptions);
    }

    // Internal so PostgresBatchOps resolves the same cached ReaderQueries instance this provider
    // uses — the dialect catalog and the single-op path reference one set of SQL strings.
    internal static ReaderQueries GetQueries(ValtuutusPostgresOptions dbOptions) =>
        QueryCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildQueries(key));

    private static ReaderQueries BuildQueries(DbQueryCacheKey key)
    {
        var relationsTable = $"{key.Schema}.{key.RelationsTable}";
        var attributesTable = $"{key.Schema}.{key.AttributesTable}";

        var selectAttributes = string.Format(UnformattedSelectAttributes, key.Schema, key.AttributesTable);
        var selectRelations = string.Format(UnformattedSelectRelations, key.Schema, key.RelationsTable);

        // {0}-placeholder templates the batched path's dialect catalog serves as-is; the single-op
        // strings below are derived from them with the fixed array parameter names, so both paths
        // execute byte-identical SQL text.
        var hasAnyDirectRelationBatchTemplate =
            $"SELECT EXISTS(SELECT 1 FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = ANY({{0}}) AND relation = @relation AND subject_id = @subject_id AND subject_relation = '')";
        var hasAnyOfDirectRelationsBatchTemplate =
            $"SELECT DISTINCT relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = ANY({{0}}) AND subject_id = @subject_id AND subject_relation = ''";
        var hasAnyOfAttributesBatchTemplate =
            $"SELECT DISTINCT attribute FROM {attributesTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = ANY({{0}}) AND value = 'true'::jsonb";

        return new ReaderQueries
        {
            SelectAttributes = selectAttributes,
            SelectRelations = selectRelations,
            GetLatestSnapToken = $"SELECT id FROM {key.Schema}.{key.TransactionsTable} ORDER BY id DESC LIMIT 1",
            Select1Attribute = $"{selectAttributes} LIMIT 1",
            ExistsRelation = string.Format(UnformattedExistsRelation, key.Schema, key.RelationsTable),
            HasDirectRelation =
                $"SELECT EXISTS(SELECT 1 FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = @relation AND subject_id = @subject_id AND subject_relation = '')",
            HasAnyDirectRelation = string.Format(hasAnyDirectRelationBatchTemplate, "@entity_ids"),
            HasAnyOfDirectRelations = string.Format(hasAnyOfDirectRelationsBatchTemplate, "@relations"),
            HasAnyOfAttributes = string.Format(hasAnyOfAttributesBatchTemplate, "@attributes"),
            HasAnyDirectRelationBatchTemplate = hasAnyDirectRelationBatchTemplate,
            HasAnyOfDirectRelationsBatchTemplate = hasAnyOfDirectRelationsBatchTemplate,
            HasAnyOfAttributesBatchTemplate = hasAnyOfAttributesBatchTemplate,
            GetIndirectRelations =
                $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = @relation AND subject_relation <> ''",
            GetRelationsWithSingleSubjectSnap =
                $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND relation = @relation AND subject_type = @subject_type AND subject_id = @subject_id",
            GetRelationsWithMultiSubjectSnap =
                $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND relation = @relation AND subject_type = @subject_type AND subject_id = ANY(@subject_ids)",
            GetRelationsWithSingleSubjectMultiRelationSnap =
                $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND relation = ANY(@relations) AND subject_type = @subject_type AND subject_id = @subject_id",
            GetRelationsWithMultiSubjectMultiRelationSnap =
                $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND relation = ANY(@relations) AND subject_type = @subject_type AND subject_id = ANY(@subject_ids)",
            GetRelationsWithEntityIdsMultiRelationSnap = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {SnapTokenPredicate}
                  AND entity_type = @entity_type
                  AND relation = ANY(@relations)
                  AND subject_type = @subject_type
                  AND entity_id = ANY(@entity_ids)
                  AND (@subject_relation IS NULL OR subject_relation = @subject_relation)
                """,
            GetRelationsJoined = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} AS r_main
                WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = @relation
                  AND r_main.subject_type = @sub_entity_type
                  AND r_main.subject_id = ANY(ARRAY(
                      SELECT entity_id FROM {relationsTable}
                      WHERE created_tx_id <= @snap_token AND (deleted_tx_id IS NULL OR deleted_tx_id > @snap_token)
                        AND entity_type = @sub_entity_type
                        AND relation = @sub_relation
                        AND subject_type = @subject_type
                        AND subject_id = @subject_id
                  ))
                """,
            GetRelationsJoinedByEntityIds = $"""
                SELECT r_dep.entity_type, r_dep.entity_id, r_dep.relation, r_dep.subject_type, r_dep.subject_id, r_dep.subject_relation
                FROM {relationsTable} AS r_dep
                WHERE r_dep.created_tx_id <= @snap_token AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > @snap_token)
                  AND r_dep.entity_type = @sub_entity_type
                  AND r_dep.relation = @sub_relation
                  AND r_dep.entity_id = ANY(ARRAY(
                      SELECT subject_id FROM {relationsTable}
                      WHERE created_tx_id <= @snap_token AND (deleted_tx_id IS NULL OR deleted_tx_id > @snap_token)
                        AND entity_type = @entity_type
                        AND entity_id = ANY(@entity_ids)
                        AND relation = @relation
                        AND subject_type = @sub_entity_type
                  ))
                """,
            HasTupleToUserSetRelation = $"""
                SELECT EXISTS(
                    SELECT 1 FROM {relationsTable} r_main
                    INNER JOIN {relationsTable} r_dep
                        ON r_dep.entity_type = r_main.subject_type AND r_dep.entity_id = r_main.subject_id
                    WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                      AND r_main.entity_type = @entity_type
                      AND r_main.entity_id = @entity_id
                      AND r_main.relation = @tuple_set_relation
                      AND r_main.subject_relation = ''
                      AND r_dep.created_tx_id <= @snap_token AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > @snap_token)
                      AND r_dep.relation = @computed_relation
                      AND r_dep.subject_type = @subject_type
                      AND r_dep.subject_id = @subject_id
                      AND r_dep.subject_relation = ''
                )
                """,
            HasUsersetJoinRelation = $"""
                SELECT (
                    EXISTS(
                        SELECT 1 FROM {relationsTable}
                        WHERE {SnapTokenPredicate}
                          AND entity_type = @entity_type
                          AND entity_id = @entity_id
                          AND relation = @relation
                          AND subject_id = @subject_id
                          AND subject_relation = ''
                    )
                    OR EXISTS(
                        SELECT 1 FROM {relationsTable} r_main
                        INNER JOIN {relationsTable} r_dep
                            ON r_dep.entity_type = r_main.subject_type AND r_dep.entity_id = r_main.subject_id
                        WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                          AND r_main.entity_type = @entity_type
                          AND r_main.entity_id = @entity_id
                          AND r_main.relation = @relation
                          AND r_main.subject_relation = @computed_relation
                          AND r_dep.created_tx_id <= @snap_token AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > @snap_token)
                          AND r_dep.relation = @computed_relation
                          AND r_dep.subject_type = @subject_type
                          AND r_dep.subject_id = @subject_id
                          AND r_dep.subject_relation = ''
                    )
                )
                """,
            GetAttribute =
                $"SELECT entity_type, entity_id, attribute, value FROM {attributesTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND attribute = @attribute LIMIT 1",
            GetAttributeWithEntityId =
                $"SELECT entity_type, entity_id, attribute, value FROM {attributesTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND attribute = @attribute AND entity_id = @entity_id LIMIT 1",
            HasTrueBoolAttribute =
                $"SELECT EXISTS(SELECT 1 FROM {attributesTable} WHERE {SnapTokenPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = @attribute AND value = 'true'::jsonb)",
            GetRelationsWithSingleSubjectScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = @relation
                  AND r_main.subject_type = @subject_type
                  AND r_main.subject_id = @subject_id
                """,
            GetRelationsWithMultiSubjectScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = @relation
                  AND r_main.subject_type = @subject_type
                  AND r_main.subject_id = ANY(@subject_ids)
                """,
            GetRelationsWithSingleSubjectMultiRelationScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = ANY(@relations)
                  AND r_main.subject_type = @subject_type
                  AND r_main.subject_id = @subject_id
                """,
            GetRelationsWithMultiSubjectMultiRelationScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = ANY(@relations)
                  AND r_main.subject_type = @subject_type
                  AND r_main.subject_id = ANY(@subject_ids)
                """,
            GetRelationsJoinedScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} AS r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                WHERE r_main.created_tx_id <= @snap_token AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @snap_token)
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = @relation
                  AND r_main.subject_type = @sub_entity_type
                  AND r_main.subject_id = ANY(ARRAY(
                      SELECT entity_id FROM {relationsTable}
                      WHERE created_tx_id <= @snap_token AND (deleted_tx_id IS NULL OR deleted_tx_id > @snap_token)
                        AND entity_type = @sub_entity_type
                        AND relation = @sub_relation
                        AND subject_type = @subject_type
                        AND subject_id = @subject_id
                  ))
                """,
            GetAttributesDictScoped = $"""
                SELECT a.entity_type, a.entity_id, a.attribute, a.value
                FROM {attributesTable} a
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = a.entity_type
                    AND r_scope.entity_id = a.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= @snap_token AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @snap_token)
                WHERE a.created_tx_id <= @snap_token AND (a.deleted_tx_id IS NULL OR a.deleted_tx_id > @snap_token)
                  AND a.entity_type = @entity_type
                  AND a.attribute = ANY(@attributes)
                """,
            GetEntityIdsExcluding = $"""
                SELECT DISTINCT entity_id
                FROM (
                    SELECT entity_id FROM {relationsTable}
                    WHERE {SnapTokenPredicate} AND entity_type = @entity_type
                    UNION
                    SELECT entity_id FROM {attributesTable}
                    WHERE {SnapTokenPredicate} AND entity_type = @entity_type
                ) universe
                WHERE entity_id <> ALL(@exclude_ids)
                """,
            GetSubjectIdsExcluding = $"""
                SELECT DISTINCT subject_id
                FROM {relationsTable}
                WHERE {SnapTokenPredicate}
                  AND subject_type = @subject_type
                  AND subject_relation = ''
                  AND subject_id <> ALL(@exclude_ids)
                """,
            SelectRelationsByTupleFilter = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {SnapTokenPredicate}
                  AND entity_type = @entity_type
                  AND entity_id = @entity_id
                  AND relation = @relation
                  AND (@subject_id IS NULL OR subject_id = @subject_id)
                  AND (@subject_relation IS NULL OR subject_relation = @subject_relation)
                  AND (@subject_type IS NULL OR subject_type = @subject_type)
                """,
            SelectRelationsByTupleFilterNoSubject = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {SnapTokenPredicate}
                  AND entity_type = @entity_type
                  AND entity_id = @entity_id
                  AND relation = @relation
                """,
            SelectRelationsWithEntityIds = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {SnapTokenPredicate}
                  AND (@subject_type IS NULL OR subject_type = @subject_type)
                  AND (@entity_type IS NULL OR entity_type = @entity_type)
                  AND (@relation IS NULL OR relation = @relation)
                  AND entity_id = ANY(@entity_ids)
                  AND (@subject_relation IS NULL OR subject_relation = @subject_relation)
                """,
            SelectAttributesByEntityAttributeFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {SnapTokenPredicate}
                  AND entity_type = @entity_type
                  AND attribute = @attribute
                  AND (@entity_id IS NULL OR entity_id = @entity_id)
                """,
            SelectAttributesByEntityAttributesFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {SnapTokenPredicate}
                  AND (@entity_id IS NULL OR entity_id = @entity_id)
                  AND entity_type = @entity_type
                  AND attribute = ANY(@attributes)
                """,
            SelectAttributesWithEntityIdsByAttributeFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {SnapTokenPredicate}
                  AND entity_type = @entity_type
                  AND attribute = @attribute
                  AND entity_id = ANY(@entity_ids)
                """,
            SelectAttributesWithEntityIdsByEntityAttributesFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {SnapTokenPredicate}
                  AND entity_type = @entity_type
                  AND attribute = ANY(@attributes)
                  AND entity_id = ANY(@entity_ids)
                """
        };
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.GetLatestSnapToken);
            var res = await command.ExecuteScalarAsync(cancellationToken) as string;
            return res is not null ? new SnapToken(res) : (SnapToken?)null;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private string PopulateGetRelationsCommand(NpgsqlParameterCollection parameters, RelationTupleFilter tupleFilter)
    {
        var noSubjectFilters = string.IsNullOrWhiteSpace(tupleFilter.SubjectId)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectRelation)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectType);

        AddStringParameter(parameters, "entity_type", tupleFilter.EntityType, 256);
        AddStringParameter(parameters, "entity_id", tupleFilter.EntityId, 64);
        AddStringParameter(parameters, "relation", tupleFilter.Relation, 64);
        if (!noSubjectFilters)
        {
            AddNullableStringParameter(parameters, "subject_id", tupleFilter.SubjectId, 64);
            AddNullableStringParameter(parameters, "subject_relation", tupleFilter.SubjectRelation, 64);
            AddNullableStringParameter(parameters, "subject_type", tupleFilter.SubjectType, 256);
        }
        AddFixedCharParameter(parameters, "snap_token", tupleFilter.SnapToken.Value, 26);

        return noSubjectFilters ? _q.SelectRelationsByTupleFilterNoSubject : _q.SelectRelationsByTupleFilter;
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand();
            command.CommandText = PopulateGetRelationsCommand(command.Parameters, tupleFilter);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    pooled.Add(ReadRelationTuple(reader));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private void PopulateHasDirectRelationCommand(NpgsqlParameterCollection parameters, RelationTupleFilter tupleFilter, string subjectId)
    {
        AddStringParameter(parameters, "entity_type", tupleFilter.EntityType, 256);
        AddStringParameter(parameters, "entity_id", tupleFilter.EntityId, 64);
        AddStringParameter(parameters, "relation", tupleFilter.Relation, 64);
        AddStringParameter(parameters, "subject_id", subjectId, 64);
        AddFixedCharParameter(parameters, "snap_token", tupleFilter.SnapToken.Value, 26);
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand();
            command.CommandText = _q.HasDirectRelation;
            PopulateHasDirectRelationCommand(command.Parameters, tupleFilter, subjectId);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private void PopulateGetIndirectRelationsCommand(NpgsqlParameterCollection parameters, RelationTupleFilter tupleFilter)
    {
        AddStringParameter(parameters, "entity_type", tupleFilter.EntityType, 256);
        AddStringParameter(parameters, "entity_id", tupleFilter.EntityId, 64);
        AddStringParameter(parameters, "relation", tupleFilter.Relation, 64);
        AddFixedCharParameter(parameters, "snap_token", tupleFilter.SnapToken.Value, 26);
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand();
            command.CommandText = _q.GetIndirectRelations;
            PopulateGetIndirectRelationsCommand(command.Parameters, tupleFilter);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    pooled.Add(new RelationTuple(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5)));
                }
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private void PopulateHasAnyDirectRelationCommand(NpgsqlParameterCollection parameters, string entityType, string[] entityIds,
        string relation, string subjectId, SnapToken snapToken)
    {
        AddStringParameter(parameters, "entity_type", entityType, 256);
        parameters.Add(new NpgsqlParameter<string[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
        {
            TypedValue = entityIds
        });
        AddStringParameter(parameters, "relation", relation, 64);
        AddStringParameter(parameters, "subject_id", subjectId, 64);
        AddFixedCharParameter(parameters, "snap_token", snapToken.Value, 26);
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand();
            command.CommandText = _q.HasAnyDirectRelation;
            PopulateHasAnyDirectRelationCommand(command.Parameters, entityType, entityIds, relation, subjectId, snapToken);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private static void PopulateHasAnyOfDirectRelationsCommand(NpgsqlParameterCollection parameters,
        string entityType, string entityId, string[] relationNames, string subjectId, SnapToken snapToken)
    {
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringParameter(parameters, "entity_id", entityId, 64);
        parameters.Add(new NpgsqlParameter<string[]>("relations", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = relationNames });
        AddStringParameter(parameters, "subject_id", subjectId, 64);
        AddFixedCharParameter(parameters, "snap_token", snapToken.Value, 26);
    }

    public async Task<HashSet<string>> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.HasAnyOfDirectRelations);
            PopulateHasAnyOfDirectRelationsCommand(command.Parameters, entityType, entityId, relationNames, subjectId, snapToken);

            var result = new HashSet<string>(relationNames.Length, StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(reader.GetString(0));
            return result;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private static void PopulateHasAnyOfAttributesCommand(NpgsqlParameterCollection parameters,
        string entityType, string entityId, string[] attributeNames, SnapToken snapToken)
    {
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringParameter(parameters, "entity_id", entityId, 64);
        parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = attributeNames });
        AddFixedCharParameter(parameters, "snap_token", snapToken.Value, 26);
    }

    public async Task<HashSet<string>> HasAnyOfAttributes(string entityType, string entityId, string[] attributeNames,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.HasAnyOfAttributes);
            PopulateHasAnyOfAttributesCommand(command.Parameters, entityType, entityId, attributeNames, snapToken);

            var result = new HashSet<string>(attributeNames.Length, StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(reader.GetString(0));
            return result;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectRelationsWithEntityIds);
            AddNullableStringParameter(command.Parameters, "subject_type", subjectType, 256);
            AddNullableStringParameter(command.Parameters, "entity_type", entityRelationFilter.EntityType, 256);
            AddNullableStringParameter(command.Parameters, "relation", entityRelationFilter.Relation, 64);
            command.Parameters.Add(new NpgsqlParameter<string[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                TypedValue = entityIds as string[] ?? entityIds.ToArray()
            });
            AddNullableStringParameter(command.Parameters, "subject_relation", subjectRelation, 64);
            AddFixedCharParameter(command.Parameters, "snap_token", entityRelationFilter.SnapToken.Value, 26);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    pooled.Add(ReadRelationTuple(reader));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIds(EntityRelationFilter entityFilter, string[] subjectsIds, string subjectType, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            if (scope is { } s)
            {
                var sql = subjectsIds.Length == 1
                    ? _q.GetRelationsWithSingleSubjectScoped
                    : _q.GetRelationsWithMultiSubjectScoped;
                await using var command = _hotPathDataSource.CreateCommand(sql);
                AddStringParameter(command.Parameters, "entity_type", entityFilter.EntityType, 256);
                AddStringParameter(command.Parameters, "relation", entityFilter.Relation, 64);
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                AddFixedCharParameter(command.Parameters, "snap_token", entityFilter.SnapToken.Value, 26);
                AddStringParameter(command.Parameters, "scope_relation", s.Relation, 64);
                AddStringParameter(command.Parameters, "scope_subject_type", s.SubjectType, 256);
                AddStringParameter(command.Parameters, "scope_subject_id", s.SubjectId, 64);
                if (subjectsIds.Length == 1)
                    AddStringParameter(command.Parameters, "subject_id", subjectsIds[0], 64);
                else
                    command.Parameters.Add(new NpgsqlParameter<string[]>("subject_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = subjectsIds });

                var scoped = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        scoped.Add(new RelationTuple(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                    return scoped;
                }
                catch { scoped.Dispose(); throw; }
            }

            if (subjectsIds.Length == 1)
            {
                await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithSingleSubjectSnap);
                AddStringParameter(command.Parameters, "entity_type", entityFilter.EntityType, 256);
                AddStringParameter(command.Parameters, "relation", entityFilter.Relation, 64);
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                AddStringParameter(command.Parameters, "subject_id", subjectsIds[0], 64);
                AddFixedCharParameter(command.Parameters, "snap_token", entityFilter.SnapToken.Value, 26);

                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        pooled.Add(new RelationTuple(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5)));
                    }
                    return pooled;
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
            }

            {
                await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithMultiSubjectSnap);
                AddStringParameter(command.Parameters, "entity_type", entityFilter.EntityType, 256);
                AddStringParameter(command.Parameters, "relation", entityFilter.Relation, 64);
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("subject_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
                {
                    TypedValue = subjectsIds
                });
                AddFixedCharParameter(command.Parameters, "snap_token", entityFilter.SnapToken.Value, 26);

                var multi = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        multi.Add(new RelationTuple(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5)));
                    }
                    return multi;
                }
                catch
                {
                    multi.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithSubjectsIdsMultiRelation(
        string entityType, string[] relationNames, string[] subjectsIds, string subjectType,
        SnapToken snapToken, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            if (scope is { } s)
            {
                var sql = subjectsIds.Length == 1
                    ? _q.GetRelationsWithSingleSubjectMultiRelationScoped
                    : _q.GetRelationsWithMultiSubjectMultiRelationScoped;
                await using var command = _hotPathDataSource.CreateCommand(sql);
                AddStringParameter(command.Parameters, "entity_type", entityType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("relations", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = relationNames });
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                AddFixedCharParameter(command.Parameters, "snap_token", snapToken.Value, 26);
                AddStringParameter(command.Parameters, "scope_relation", s.Relation, 64);
                AddStringParameter(command.Parameters, "scope_subject_type", s.SubjectType, 256);
                AddStringParameter(command.Parameters, "scope_subject_id", s.SubjectId, 64);
                if (subjectsIds.Length == 1)
                    AddStringParameter(command.Parameters, "subject_id", subjectsIds[0], 64);
                else
                    command.Parameters.Add(new NpgsqlParameter<string[]>("subject_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = subjectsIds });

                var scoped = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        scoped.Add(new RelationTuple(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                    return scoped;
                }
                catch { scoped.Dispose(); throw; }
            }

            if (subjectsIds.Length == 1)
            {
                await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithSingleSubjectMultiRelationSnap);
                AddStringParameter(command.Parameters, "entity_type", entityType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("relations", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = relationNames });
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                AddStringParameter(command.Parameters, "subject_id", subjectsIds[0], 64);
                AddFixedCharParameter(command.Parameters, "snap_token", snapToken.Value, 26);

                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        pooled.Add(new RelationTuple(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                    return pooled;
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
            }

            {
                await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithMultiSubjectMultiRelationSnap);
                AddStringParameter(command.Parameters, "entity_type", entityType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("relations", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = relationNames });
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("subject_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = subjectsIds });
                AddFixedCharParameter(command.Parameters, "snap_token", snapToken.Value, 26);

                var multi = PooledList<RelationTuple>.Rent();
                try
                {
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        multi.Add(new RelationTuple(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                    return multi;
                }
                catch
                {
                    multi.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIdsMultiRelation(
        string entityType, string[] relationNames, string subjectType, IEnumerable<string> entityIds,
        string? subjectRelation, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithEntityIdsMultiRelationSnap);
            AddStringParameter(command.Parameters, "entity_type", entityType, 256);
            command.Parameters.Add(new NpgsqlParameter<string[]>("relations", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = relationNames });
            AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
            command.Parameters.Add(new NpgsqlParameter<string[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                TypedValue = entityIds as string[] ?? entityIds.ToArray()
            });
            AddNullableStringParameter(command.Parameters, "subject_relation", subjectRelation, 64);
            AddFixedCharParameter(command.Parameters, "snap_token", snapToken.Value, 26);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    pooled.Add(ReadRelationTuple(reader));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter, string subEntityType, string subRelation,
        string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            var sql = scope.HasValue ? _q.GetRelationsJoinedScoped : _q.GetRelationsJoined;
            await using var command = _hotPathDataSource.CreateCommand(sql);
            AddFixedCharParameter(command.Parameters, "snap_token", mainFilter.SnapToken.Value, 26);
            AddStringParameter(command.Parameters, "entity_type", mainFilter.EntityType, 256);
            AddStringParameter(command.Parameters, "relation", mainFilter.Relation, 64);
            AddStringParameter(command.Parameters, "sub_entity_type", subEntityType, 256);
            AddStringParameter(command.Parameters, "sub_relation", subRelation, 64);
            AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
            AddStringParameter(command.Parameters, "subject_id", subjectId, 64);
            if (scope is { } s)
            {
                AddStringParameter(command.Parameters, "scope_relation", s.Relation, 64);
                AddStringParameter(command.Parameters, "scope_subject_type", s.SubjectType, 256);
                AddStringParameter(command.Parameters, "scope_subject_id", s.SubjectId, 64);
            }

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    pooled.Add(new RelationTuple(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                }
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsJoinedByEntityIds(
        EntityRelationFilter mainFilter, IEnumerable<string> entityIds, string subEntityType, string subRelation,
        CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsJoinedByEntityIds);
            AddFixedCharParameter(command.Parameters, "snap_token", mainFilter.SnapToken.Value, 26);
            AddStringParameter(command.Parameters, "entity_type", mainFilter.EntityType, 256);
            AddStringParameter(command.Parameters, "relation", mainFilter.Relation, 64);
            command.Parameters.Add(new NpgsqlParameter<string[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                TypedValue = entityIds as string[] ?? entityIds.ToArray()
            });
            AddStringParameter(command.Parameters, "sub_entity_type", subEntityType, 256);
            AddStringParameter(command.Parameters, "sub_relation", subRelation, 64);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    pooled.Add(new RelationTuple(
                        reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                }
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private void PopulateHasTupleToUserSetRelationCommand(NpgsqlParameterCollection parameters,
        string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken)
    {
        AddFixedCharParameter(parameters, "snap_token", snapToken.Value, 26);
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringParameter(parameters, "entity_id", entityId, 64);
        AddStringParameter(parameters, "tuple_set_relation", tupleSetRelation, 64);
        AddStringParameter(parameters, "computed_relation", computedRelation, 64);
        AddStringParameter(parameters, "subject_type", subjectType, 256);
        AddStringParameter(parameters, "subject_id", subjectId, 64);
    }

    public async Task<bool> HasTupleToUserSetRelation(
        string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand();
            command.CommandText = _q.HasTupleToUserSetRelation;
            PopulateHasTupleToUserSetRelationCommand(command.Parameters, entityType, entityId, tupleSetRelation,
                subEntityType, computedRelation, subjectType, subjectId, snapToken);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private void PopulateHasUsersetJoinRelationCommand(NpgsqlParameterCollection parameters,
        string entityType, string entityId, string relation, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken)
    {
        AddFixedCharParameter(parameters, "snap_token", snapToken.Value, 26);
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringParameter(parameters, "entity_id", entityId, 64);
        AddStringParameter(parameters, "relation", relation, 64);
        AddStringParameter(parameters, "computed_relation", computedRelation, 64);
        AddStringParameter(parameters, "subject_type", subjectType, 256);
        AddStringParameter(parameters, "subject_id", subjectId, 64);
    }

    // subEntityType is accepted for interface-signature symmetry with HasTupleToUserSetRelation
    // but isn't a runtime filter here — it's what proved the join eligible at compile time; the
    // query only needs relation/computedRelation/subjectType/subjectId.
    public async Task<bool> HasUsersetJoinRelation(
        string entityType, string entityId, string relation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand();
            command.CommandText = _q.HasUsersetJoinRelation;
            PopulateHasUsersetJoinRelationCommand(command.Parameters, entityType, entityId, relation,
                computedRelation, subjectType, subjectId, snapToken);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            var hasEntityId = !string.IsNullOrWhiteSpace(filter.EntityId);
            await using var command = _hotPathDataSource.CreateCommand(hasEntityId ? _q.GetAttributeWithEntityId : _q.GetAttribute);

            AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
            AddStringParameter(command.Parameters, "attribute", filter.Attribute, 64);
            if (hasEntityId)
                AddStringParameter(command.Parameters, "entity_id", filter.EntityId!, 64);
            AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadAttributeTuple(reader);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private void PopulateHasTrueBoolAttributeCommand(NpgsqlParameterCollection parameters, string entityType, string entityId,
        string attribute, SnapToken snapToken)
    {
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringParameter(parameters, "entity_id", entityId, 64);
        AddStringParameter(parameters, "attribute", attribute, 64);
        AddFixedCharParameter(parameters, "snap_token", snapToken.Value, 26);
    }

    public async Task<bool> HasTrueBoolAttribute(string entityType, string entityId, string attribute,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand();
            command.CommandText = _q.HasTrueBoolAttribute;
            PopulateHasTrueBoolAttributeCommand(command.Parameters, entityType, entityId, attribute, snapToken);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectAttributesByEntityAttributeFilter);
            AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
            AddStringParameter(command.Parameters, "attribute", filter.Attribute, 64);
            AddNullableStringParameter(command.Parameters, "entity_id", filter.EntityId, 64);
            AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);

            var rows = new List<AttributeTuple>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(ReadAttributeTuple(reader));
            return rows;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            if (scope is { } s)
            {
                await using var command = _hotPathDataSource.CreateCommand(_q.GetAttributesDictScoped);
                AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = filter.Attributes });
                AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);
                AddStringParameter(command.Parameters, "scope_relation", s.Relation, 64);
                AddStringParameter(command.Parameters, "scope_subject_type", s.SubjectType, 256);
                AddStringParameter(command.Parameters, "scope_subject_id", s.SubjectId, 64);

                var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var tuple = ReadAttributeTuple(reader);
                    dict[(tuple.Attribute, tuple.EntityId)] = tuple;
                }
                return dict;
            }

            await using var unscopedCommand = _hotPathDataSource.CreateCommand(_q.SelectAttributesByEntityAttributesFilter);
            AddNullableStringParameter(unscopedCommand.Parameters, "entity_id", filter.EntityId, 64);
            AddStringParameter(unscopedCommand.Parameters, "entity_type", filter.EntityType, 256);
            unscopedCommand.Parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = filter.Attributes });
            AddFixedCharParameter(unscopedCommand.Parameters, "snap_token", filter.SnapToken.Value, 26);

            var unscopedResult = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
            await using var unscopedReader = await unscopedCommand.ExecuteReaderAsync(cancellationToken);
            while (await unscopedReader.ReadAsync(cancellationToken))
            {
                var tuple = ReadAttributeTuple(unscopedReader);
                unscopedResult[(tuple.Attribute, tuple.EntityId)] = tuple;
            }
            return unscopedResult;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectAttributesByEntityAttributesFilter);
            AddNullableStringParameter(command.Parameters, "entity_id", filter.EntityId, 64);
            AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
            command.Parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = filter.Attributes });
            AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);

            var pooled = PooledList<AttributeTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    pooled.Add(ReadAttributeTuple(reader));
                return pooled;
            }
            catch
            {
                pooled.Dispose();
                throw;
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributesWithEntityIds(AttributeFilter filter, IEnumerable<string> entitiesIds, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectAttributesWithEntityIdsByAttributeFilter);
            AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
            AddStringParameter(command.Parameters, "attribute", filter.Attribute, 64);
            command.Parameters.Add(new NpgsqlParameter<string[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                TypedValue = entitiesIds as string[] ?? entitiesIds.ToArray()
            });
            AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);

            var rows = new List<AttributeTuple>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(ReadAttributeTuple(reader));
            return rows;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributesWithEntityIds(EntityAttributesFilter filter, IEnumerable<string> entitiesIds,
        CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectAttributesWithEntityIdsByEntityAttributesFilter);
            AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
            command.Parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = filter.Attributes });
            command.Parameters.Add(new NpgsqlParameter<string[]>("entity_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar)
            {
                TypedValue = entitiesIds as string[] ?? entitiesIds.ToArray()
            });
            AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);

            var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var tuple = ReadAttributeTuple(reader);
                dict[(tuple.Attribute, tuple.EntityId)] = tuple;
            }
            return dict;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var conn = await _hotPathDataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = _q.GetEntityIdsExcluding;
            cmd.Parameters.Add(new NpgsqlParameter<string>("entity_type", NpgsqlDbType.Varchar) { TypedValue = entityType });
            cmd.Parameters.Add(new NpgsqlParameter<string>("snap_token", NpgsqlDbType.Char) { Size = 26, TypedValue = snapToken.Value });
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("exclude_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = excludeIds.ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var result = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
                result.Add(reader.GetString(0));
            return result;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var conn = await _hotPathDataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = _q.GetSubjectIdsExcluding;
            cmd.Parameters.Add(new NpgsqlParameter<string>("subject_type", NpgsqlDbType.Varchar) { TypedValue = subjectType });
            cmd.Parameters.Add(new NpgsqlParameter<string>("snap_token", NpgsqlDbType.Char) { Size = 26, TypedValue = snapToken.Value });
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("exclude_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = excludeIds.ToArray() });
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var result = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
                result.Add(reader.GetString(0));
            return result;
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
