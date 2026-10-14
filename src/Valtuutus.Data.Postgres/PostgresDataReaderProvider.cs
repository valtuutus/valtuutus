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
    internal const string BatchResolvedSnapTokenSetting = "valtuutus.snap_token";
    // Same char(26) type as the @snap_token parameter, so predicates keep their index usage. A
    // scalar subquery, so it is evaluated once per statement (an InitPlan compared like a
    // parameter) instead of once per filtered row like a bare function call.
    internal const string BatchResolvedSnapTokenExpression = $"(SELECT current_setting('{BatchResolvedSnapTokenSetting}')::char(26))";
    // Internal so FusedExpressionSql, which builds the fused-expression fragments for both this
    // class's HasFusedExpression and PostgresBatchOps.AddHasFusedExpressionToBatch, uses the
    // exact snap-token predicate text those queries use.
    internal const string SnapTokenPredicate = "created_tx_id <= @snap_token AND (deleted_tx_id IS NULL OR deleted_tx_id > @snap_token)";
    internal static string SnapTokenPredicateFor(string snap) =>
        $"created_tx_id <= {snap} AND (deleted_tx_id IS NULL OR deleted_tx_id > {snap})";

    // Internal so PostgresBatchOps' dialect catalog serves the exact SQL text of these single-op
    // queries: one definition, shared through GetQueries, keeps the two paths identical and lets
    // the server's prepared-statement cache hold a single statement for both.
    internal sealed record ReaderQueries
    {
        public required string RelationsTable { get; init; }
        public required string AttributesTable { get; init; }
        public required string GetLatestSnapToken { get; init; }
        // Batch form of GetLatestSnapToken for PostgresBatchOps: parks the latest token in a
        // session setting (read back through BatchResolvedSnapTokenExpression) and returns it.
        public required string ResolveLatestSnapTokenForBatch { get; init; }
        public required string ResolveLatestSnapTokenForBatchWithGenericPlans { get; init; }
        public required PgStatement HasDirectRelation { get; init; }
        public required PgStatement HasAnyDirectRelation { get; init; }
        public required PgStatement HasAnyOfDirectRelations { get; init; }
        public required PgStatement HasAnyOfAttributes { get; init; }
        public required PgStatement GetIndirectRelations { get; init; }
        public required PgStatement GetRelationsWithSingleSubjectSnap { get; init; }
        public required PgStatement GetRelationsWithMultiSubjectSnap { get; init; }
        public required PgStatement GetRelationsWithSingleSubjectMultiRelationSnap { get; init; }
        public required PgStatement GetRelationsWithMultiSubjectMultiRelationSnap { get; init; }
        public required PgStatement GetRelationsWithEntityIdsMultiRelationSnap { get; init; }
        public required PgStatement GetRelationsJoined { get; init; }
        public required PgStatement GetRelationsJoinedByEntityIds { get; init; }
        public required PgStatement HasTupleToUserSetRelation { get; init; }
        public required PgStatement HasUsersetJoinRelation { get; init; }
        public required PgStatement HasTrueBoolAttribute { get; init; }
        public required PgStatement GetRelationsWithSingleSubjectScoped { get; init; }
        public required PgStatement GetRelationsWithMultiSubjectScoped { get; init; }
        public required PgStatement GetRelationsWithSingleSubjectMultiRelationScoped { get; init; }
        public required PgStatement GetRelationsWithMultiSubjectMultiRelationScoped { get; init; }
        public required PgStatement GetRelationsJoinedScoped { get; init; }
        public required PgStatement GetAttributesDictScoped { get; init; }
        public required PgStatement GetEntityIdsExcluding { get; init; }
        public required PgStatement GetSubjectIdsExcluding { get; init; }
        public required PgStatement SelectRelationsByTupleFilter { get; init; }
        public required PgStatement SelectRelationsByTupleFilterNoSubject { get; init; }
        public required PgStatement SelectRelationsWithEntityIds { get; init; }
        public required PgStatement SelectAttributesByEntityAttributeFilter { get; init; }
        public required PgStatement SelectAttributesByEntityAttributesFilter { get; init; }
        // Single-entity form (entity id always bound) of SelectAttributesByEntityAttributesFilter.
        public required PgStatement SelectAttributesSingleEntity { get; init; }
        public required PgStatement SelectAttributesWithEntityIdsByAttributeFilter { get; init; }
        public required PgStatement SelectAttributesWithEntityIdsByEntityAttributesFilter { get; init; }
    }

    private static readonly ConcurrentDictionary<DbQueryCacheKey, ReaderQueries> QueryCache = new();
    private static readonly ConcurrentDictionary<DbQueryCacheKey, ReaderQueries> BatchResolvedSnapTokenQueryCache = new();
    private readonly ReaderQueries _q;

    private readonly DbConnectionFactory _connectionFactory;
    private readonly NpgsqlDataSource _hotPathDataSource;
    private static readonly ConcurrentDictionary<DataSourceCacheKey, NpgsqlDataSource> DataSourceCache = new();
    private readonly record struct DataSourceCacheKey(string ConnectionString, int MaxAutoPrepare, int AutoPrepareMinUsages);

    // The three parameter helpers below are internal for the same reason as ReaderQueries:
    // PostgresBatchOps' parameter hooks build their NpgsqlParameters through them, so batched
    // commands carry byte-identical parameters (same NpgsqlDbType, same Size).
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

    // Parameters of the LookupSubject statements, written by the single-command methods below and
    // by PostgresLookupSubjectBatchOps alike, so both paths bind identical parameters to one
    // prepared statement. A null snap token writes none: the batch-resolved catalog's statements
    // read the token their batch resolved.
    internal static void AddRelationsWithEntityIdsParameters(NpgsqlParameterCollection parameters,
        string entityType, string relation, string subjectType, IEnumerable<string> entityIds,
        string? subjectRelation, SnapToken? snapToken)
    {
        AddNullableStringParameter(parameters, "subject_type", subjectType, 256);
        AddNullableStringParameter(parameters, "entity_type", entityType, 256);
        AddNullableStringParameter(parameters, "relation", relation, 64);
        AddStringArrayParameter(parameters, "entity_ids", entityIds);
        AddNullableStringParameter(parameters, "subject_relation", subjectRelation, 64);
        AddSnapTokenParameter(parameters, snapToken);
    }

    internal static void AddRelationsWithEntityIdsMultiRelationParameters(NpgsqlParameterCollection parameters,
        string entityType, string[] relationNames, string subjectType, IEnumerable<string> entityIds,
        string? subjectRelation, SnapToken? snapToken)
    {
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringArrayParameter(parameters, "relations", relationNames);
        AddStringParameter(parameters, "subject_type", subjectType, 256);
        AddStringArrayParameter(parameters, "entity_ids", entityIds);
        AddNullableStringParameter(parameters, "subject_relation", subjectRelation, 64);
        AddSnapTokenParameter(parameters, snapToken);
    }

    internal static void AddRelationsJoinedByEntityIdsParameters(NpgsqlParameterCollection parameters,
        string entityType, string relation, IEnumerable<string> entityIds, string subEntityType, string subRelation,
        SnapToken? snapToken)
    {
        AddSnapTokenParameter(parameters, snapToken);
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringParameter(parameters, "relation", relation, 64);
        AddStringArrayParameter(parameters, "entity_ids", entityIds);
        AddStringParameter(parameters, "sub_entity_type", subEntityType, 256);
        AddStringParameter(parameters, "sub_relation", subRelation, 64);
    }

    internal static void AddAttributeWithEntityIdsParameters(NpgsqlParameterCollection parameters,
        string entityType, string attribute, IEnumerable<string> entityIds, SnapToken? snapToken)
    {
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringParameter(parameters, "attribute", attribute, 64);
        AddStringArrayParameter(parameters, "entity_ids", entityIds);
        AddSnapTokenParameter(parameters, snapToken);
    }

    internal static void AddAttributesWithEntityIdsParameters(NpgsqlParameterCollection parameters,
        string entityType, string[] attributes, IEnumerable<string> entityIds, SnapToken? snapToken)
    {
        AddStringParameter(parameters, "entity_type", entityType, 256);
        AddStringArrayParameter(parameters, "attributes", attributes);
        AddStringArrayParameter(parameters, "entity_ids", entityIds);
        AddSnapTokenParameter(parameters, snapToken);
    }

    internal static void AddSubjectIdsExcludingParameters(NpgsqlParameterCollection parameters,
        string subjectType, IReadOnlyCollection<string> excludeIds, SnapToken? snapToken)
    {
        parameters.Add(new NpgsqlParameter<string>("subject_type", NpgsqlDbType.Varchar) { TypedValue = subjectType });
        AddSnapTokenParameter(parameters, snapToken);
        AddStringArrayParameter(parameters, "exclude_ids", excludeIds);
    }

    private static void AddStringArrayParameter(NpgsqlParameterCollection parameters, string name,
        IEnumerable<string> values)
    {
        parameters.Add(new NpgsqlParameter<string[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Varchar)
        {
            TypedValue = values as string[] ?? values.ToArray()
        });
    }

    private static void AddSnapTokenParameter(NpgsqlParameterCollection parameters, SnapToken? snapToken)
    {
        if (snapToken is { } token)
            AddFixedCharParameter(parameters, "snap_token", token.Value, 26);
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
        QueryCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildQueries(key, "@snap_token"));

    // Same queries, with every snap-token predicate reading the token resolved earlier in the same batch
    // (see PostgresBatchOps.UsingBatchResolvedSnapToken) instead of the @snap_token parameter.
    internal static ReaderQueries GetBatchResolvedSnapTokenQueries(ValtuutusPostgresOptions dbOptions) =>
        BatchResolvedSnapTokenQueryCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildQueries(key, BatchResolvedSnapTokenExpression));

    // `snap` is the SQL expression every snap-token predicate compares against: the {snap}
    // parameter, or BatchResolvedSnapTokenExpression for the batch-resolved-token catalog.
    private static ReaderQueries BuildQueries(DbQueryCacheKey key, string snap)
    {
        var snapPredicate = SnapTokenPredicateFor(snap);
        var relationsTable = $"{key.Schema}.{key.RelationsTable}";
        var attributesTable = $"{key.Schema}.{key.AttributesTable}";

        return new ReaderQueries
        {
            RelationsTable = relationsTable,
            AttributesTable = attributesTable,
            GetLatestSnapToken = $"SELECT id FROM {key.Schema}.{key.TransactionsTable} ORDER BY id DESC LIMIT 1",
            ResolveLatestSnapTokenForBatch =
                $"SELECT set_config('{BatchResolvedSnapTokenSetting}', COALESCE((SELECT id FROM {key.Schema}.{key.TransactionsTable} ORDER BY id DESC LIMIT 1), '{SnapToken.MinValue.Value}'), false)",
            // Also pins generic plans for the rest of this batch only (is_local: the batch's implicit
            // transaction). The batch's snap-token predicates read a session setting the planner
            // can't see, so custom and generic plans cost about the same, and depending on table
            // statistics the plan cache can settle on re-planning every execution; the
            // parameter-bound catalog's statements settle on their generic plan instead. Only for
            // auto-prepared statements: an unprepared statement is planned on every execution
            // anyway, and a generic plan there just loses the parameter values.
            ResolveLatestSnapTokenForBatchWithGenericPlans =
                $"SELECT set_config('{BatchResolvedSnapTokenSetting}', COALESCE((SELECT id FROM {key.Schema}.{key.TransactionsTable} ORDER BY id DESC LIMIT 1), '{SnapToken.MinValue.Value}'), false), set_config('plan_cache_mode', 'force_generic_plan', true)",
            HasDirectRelation = PgStatement.Compile($"SELECT EXISTS(SELECT 1 FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = @relation AND subject_id = @subject_id AND subject_relation = '')"),
            HasAnyDirectRelation = PgStatement.Compile($"SELECT EXISTS(SELECT 1 FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = ANY(@entity_ids) AND relation = @relation AND subject_id = @subject_id AND subject_relation = '')"),
            HasAnyOfDirectRelations = PgStatement.Compile($"SELECT DISTINCT relation FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = ANY(@relations) AND subject_id = @subject_id AND subject_relation = ''"),
            HasAnyOfAttributes = PgStatement.Compile($"SELECT DISTINCT attribute FROM {attributesTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = ANY(@attributes) AND value = 'true'::jsonb"),
            GetIndirectRelations = PgStatement.Compile($"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND relation = @relation AND subject_relation <> ''"),
            GetRelationsWithSingleSubjectSnap = PgStatement.Compile($"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND relation = @relation AND subject_type = @subject_type AND subject_id = @subject_id"),
            GetRelationsWithMultiSubjectSnap = PgStatement.Compile($"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND relation = @relation AND subject_type = @subject_type AND subject_id = ANY(@subject_ids)"),
            GetRelationsWithSingleSubjectMultiRelationSnap = PgStatement.Compile($"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND relation = ANY(@relations) AND subject_type = @subject_type AND subject_id = @subject_id"),
            GetRelationsWithMultiSubjectMultiRelationSnap = PgStatement.Compile($"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE {snapPredicate} AND entity_type = @entity_type AND relation = ANY(@relations) AND subject_type = @subject_type AND subject_id = ANY(@subject_ids)"),
            GetRelationsWithEntityIdsMultiRelationSnap = PgStatement.Compile($"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @entity_type
                  AND relation = ANY(@relations)
                  AND subject_type = @subject_type
                  AND entity_id = ANY(@entity_ids)
                  AND (@subject_relation IS NULL OR subject_relation = @subject_relation)
                """),
            GetRelationsJoined = PgStatement.Compile($"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} AS r_main
                WHERE r_main.created_tx_id <= {snap} AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > {snap})
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = @relation
                  AND r_main.subject_type = @sub_entity_type
                  AND r_main.subject_id = ANY(ARRAY(
                      SELECT entity_id FROM {relationsTable}
                      WHERE created_tx_id <= {snap} AND (deleted_tx_id IS NULL OR deleted_tx_id > {snap})
                        AND entity_type = @sub_entity_type
                        AND relation = @sub_relation
                        AND subject_type = @subject_type
                        AND subject_id = @subject_id
                  ))
                """),
            GetRelationsJoinedByEntityIds = PgStatement.Compile($"""
                SELECT r_dep.entity_type, r_dep.entity_id, r_dep.relation, r_dep.subject_type, r_dep.subject_id, r_dep.subject_relation
                FROM {relationsTable} AS r_dep
                WHERE r_dep.created_tx_id <= {snap} AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > {snap})
                  AND r_dep.entity_type = @sub_entity_type
                  AND r_dep.relation = @sub_relation
                  AND r_dep.entity_id = ANY(ARRAY(
                      SELECT subject_id FROM {relationsTable}
                      WHERE created_tx_id <= {snap} AND (deleted_tx_id IS NULL OR deleted_tx_id > {snap})
                        AND entity_type = @entity_type
                        AND entity_id = ANY(@entity_ids)
                        AND relation = @relation
                        AND subject_type = @sub_entity_type
                  ))
                """),
            HasTupleToUserSetRelation = PgStatement.Compile($"""
                SELECT EXISTS(
                    SELECT 1 FROM {relationsTable} r_main
                    INNER JOIN {relationsTable} r_dep
                        ON r_dep.entity_type = r_main.subject_type AND r_dep.entity_id = r_main.subject_id
                    WHERE r_main.created_tx_id <= {snap} AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > {snap})
                      AND r_main.entity_type = @entity_type
                      AND r_main.entity_id = @entity_id
                      AND r_main.relation = @tuple_set_relation
                      AND r_main.subject_relation = ''
                      AND r_dep.created_tx_id <= {snap} AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > {snap})
                      AND r_dep.relation = @computed_relation
                      AND r_dep.subject_type = @subject_type
                      AND r_dep.subject_id = @subject_id
                      AND r_dep.subject_relation = ''
                )
                """),
            HasUsersetJoinRelation = PgStatement.Compile($"""
                SELECT (
                    EXISTS(
                        SELECT 1 FROM {relationsTable}
                        WHERE {snapPredicate}
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
                        WHERE r_main.created_tx_id <= {snap} AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > {snap})
                          AND r_main.entity_type = @entity_type
                          AND r_main.entity_id = @entity_id
                          AND r_main.relation = @relation
                          AND r_main.subject_relation = @computed_relation
                          AND r_dep.created_tx_id <= {snap} AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > {snap})
                          AND r_dep.relation = @computed_relation
                          AND r_dep.subject_type = @subject_type
                          AND r_dep.subject_id = @subject_id
                          AND r_dep.subject_relation = ''
                    )
                )
                """),
            HasTrueBoolAttribute = PgStatement.Compile($"SELECT EXISTS(SELECT 1 FROM {attributesTable} WHERE {snapPredicate} AND entity_type = @entity_type AND entity_id = @entity_id AND attribute = @attribute AND value = 'true'::jsonb)"),
            GetRelationsWithSingleSubjectScoped = PgStatement.Compile($"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= {snap} AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > {snap})
                WHERE r_main.created_tx_id <= {snap} AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > {snap})
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = @relation
                  AND r_main.subject_type = @subject_type
                  AND r_main.subject_id = @subject_id
                """),
            GetRelationsWithMultiSubjectScoped = PgStatement.Compile($"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= {snap} AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > {snap})
                WHERE r_main.created_tx_id <= {snap} AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > {snap})
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = @relation
                  AND r_main.subject_type = @subject_type
                  AND r_main.subject_id = ANY(@subject_ids)
                """),
            GetRelationsWithSingleSubjectMultiRelationScoped = PgStatement.Compile($"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= {snap} AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > {snap})
                WHERE r_main.created_tx_id <= {snap} AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > {snap})
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = ANY(@relations)
                  AND r_main.subject_type = @subject_type
                  AND r_main.subject_id = @subject_id
                """),
            GetRelationsWithMultiSubjectMultiRelationScoped = PgStatement.Compile($"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= {snap} AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > {snap})
                WHERE r_main.created_tx_id <= {snap} AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > {snap})
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = ANY(@relations)
                  AND r_main.subject_type = @subject_type
                  AND r_main.subject_id = ANY(@subject_ids)
                """),
            GetRelationsJoinedScoped = PgStatement.Compile($"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} AS r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= {snap} AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > {snap})
                WHERE r_main.created_tx_id <= {snap} AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > {snap})
                  AND r_main.entity_type = @entity_type
                  AND r_main.relation = @relation
                  AND r_main.subject_type = @sub_entity_type
                  AND r_main.subject_id = ANY(ARRAY(
                      SELECT entity_id FROM {relationsTable}
                      WHERE created_tx_id <= {snap} AND (deleted_tx_id IS NULL OR deleted_tx_id > {snap})
                        AND entity_type = @sub_entity_type
                        AND relation = @sub_relation
                        AND subject_type = @subject_type
                        AND subject_id = @subject_id
                  ))
                """),
            GetAttributesDictScoped = PgStatement.Compile($"""
                SELECT a.entity_type, a.entity_id, a.attribute, a.value
                FROM {attributesTable} a
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = a.entity_type
                    AND r_scope.entity_id = a.entity_id
                    AND r_scope.relation = @scope_relation
                    AND r_scope.subject_type = @scope_subject_type
                    AND r_scope.subject_id = @scope_subject_id
                    AND r_scope.created_tx_id <= {snap} AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > {snap})
                WHERE a.created_tx_id <= {snap} AND (a.deleted_tx_id IS NULL OR a.deleted_tx_id > {snap})
                  AND a.entity_type = @entity_type
                  AND a.attribute = ANY(@attributes)
                """),
            GetEntityIdsExcluding = PgStatement.Compile($"""
                SELECT DISTINCT entity_id
                FROM (
                    SELECT entity_id FROM {relationsTable}
                    WHERE {snapPredicate} AND entity_type = @entity_type
                    UNION
                    SELECT entity_id FROM {attributesTable}
                    WHERE {snapPredicate} AND entity_type = @entity_type
                ) universe
                WHERE entity_id <> ALL(@exclude_ids)
                """),
            GetSubjectIdsExcluding = PgStatement.Compile($"""
                SELECT DISTINCT subject_id
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND subject_type = @subject_type
                  AND subject_relation = ''
                  AND subject_id <> ALL(@exclude_ids)
                """),
            SelectRelationsByTupleFilter = PgStatement.Compile($"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @entity_type
                  AND entity_id = @entity_id
                  AND relation = @relation
                  AND (@subject_id IS NULL OR subject_id = @subject_id)
                  AND (@subject_relation IS NULL OR subject_relation = @subject_relation)
                  AND (@subject_type IS NULL OR subject_type = @subject_type)
                """),
            SelectRelationsByTupleFilterNoSubject = PgStatement.Compile($"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @entity_type
                  AND entity_id = @entity_id
                  AND relation = @relation
                """),
            // The columns the userset index leads with are plain equalities: a generic plan, which
            // batches that resolve their own snap token force, can seek only through those, and
            // an "@p IS NULL OR col = @p" filter on them turns it into a scan of the whole table.
            SelectRelationsWithEntityIds = PgStatement.Compile($"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND subject_type = @subject_type
                  AND entity_type = @entity_type
                  AND relation = @relation
                  AND entity_id = ANY(@entity_ids)
                  AND (@subject_relation IS NULL OR subject_relation = @subject_relation)
                """),
            SelectAttributesByEntityAttributeFilter = PgStatement.Compile($"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @entity_type
                  AND attribute = @attribute
                  AND (@entity_id IS NULL OR entity_id = @entity_id)
                """),
            SelectAttributesSingleEntity = PgStatement.Compile($"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @entity_type
                  AND entity_id = @entity_id
                  AND attribute = ANY(@attributes)
                """),
            SelectAttributesByEntityAttributesFilter = PgStatement.Compile($"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND (@entity_id IS NULL OR entity_id = @entity_id)
                  AND entity_type = @entity_type
                  AND attribute = ANY(@attributes)
                """),
            SelectAttributesWithEntityIdsByAttributeFilter = PgStatement.Compile($"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @entity_type
                  AND attribute = @attribute
                  AND entity_id = ANY(@entity_ids)
                """),
            SelectAttributesWithEntityIdsByEntityAttributesFilter = PgStatement.Compile($"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @entity_type
                  AND attribute = ANY(@attributes)
                  AND entity_id = ANY(@entity_ids)
                """)
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

    private PgStatement PopulateGetRelationsCommand(NpgsqlParameterCollection parameters, RelationTupleFilter tupleFilter)
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
            var statement = PopulateGetRelationsCommand(command.Parameters, tupleFilter);
            command.CommandText = statement.Sql;
            statement.Bind(command.Parameters);

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
            command.CommandText = _q.HasDirectRelation.Sql;
            PopulateHasDirectRelationCommand(command.Parameters, tupleFilter, subjectId);
            _q.HasDirectRelation.Bind(command.Parameters);
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
            command.CommandText = _q.GetIndirectRelations.Sql;
            PopulateGetIndirectRelationsCommand(command.Parameters, tupleFilter);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                _q.GetIndirectRelations.Bind(command.Parameters);
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
            command.CommandText = _q.HasAnyDirectRelation.Sql;
            PopulateHasAnyDirectRelationCommand(command.Parameters, entityType, entityIds, relation, subjectId, snapToken);

            _q.HasAnyDirectRelation.Bind(command.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.HasAnyOfDirectRelations.Sql);
            PopulateHasAnyOfDirectRelationsCommand(command.Parameters, entityType, entityId, relationNames, subjectId, snapToken);

            var result = new HashSet<string>(relationNames.Length, StringComparer.Ordinal);
            _q.HasAnyOfDirectRelations.Bind(command.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.HasAnyOfAttributes.Sql);
            PopulateHasAnyOfAttributesCommand(command.Parameters, entityType, entityId, attributeNames, snapToken);

            var result = new HashSet<string>(attributeNames.Length, StringComparer.Ordinal);
            _q.HasAnyOfAttributes.Bind(command.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectRelationsWithEntityIds.Sql);
            AddRelationsWithEntityIdsParameters(command.Parameters, entityRelationFilter.EntityType,
                entityRelationFilter.Relation, subjectType, entityIds, subjectRelation, entityRelationFilter.SnapToken);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                _q.SelectRelationsWithEntityIds.Bind(command.Parameters);
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
                var statement = subjectsIds.Length == 1
                    ? _q.GetRelationsWithSingleSubjectScoped
                    : _q.GetRelationsWithMultiSubjectScoped;
                await using var command = _hotPathDataSource.CreateCommand(statement.Sql);
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
                    statement.Bind(command.Parameters);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        scoped.Add(new RelationTuple(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                    return scoped;
                }
                catch { scoped.Dispose(); throw; }
            }

            if (subjectsIds.Length == 1)
            {
                await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithSingleSubjectSnap.Sql);
                AddStringParameter(command.Parameters, "entity_type", entityFilter.EntityType, 256);
                AddStringParameter(command.Parameters, "relation", entityFilter.Relation, 64);
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                AddStringParameter(command.Parameters, "subject_id", subjectsIds[0], 64);
                AddFixedCharParameter(command.Parameters, "snap_token", entityFilter.SnapToken.Value, 26);

                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    _q.GetRelationsWithSingleSubjectSnap.Bind(command.Parameters);
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
                await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithMultiSubjectSnap.Sql);
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
                    _q.GetRelationsWithMultiSubjectSnap.Bind(command.Parameters);
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
                var statement = subjectsIds.Length == 1
                    ? _q.GetRelationsWithSingleSubjectMultiRelationScoped
                    : _q.GetRelationsWithMultiSubjectMultiRelationScoped;
                await using var command = _hotPathDataSource.CreateCommand(statement.Sql);
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
                    statement.Bind(command.Parameters);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        scoped.Add(new RelationTuple(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                    return scoped;
                }
                catch { scoped.Dispose(); throw; }
            }

            if (subjectsIds.Length == 1)
            {
                await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithSingleSubjectMultiRelationSnap.Sql);
                AddStringParameter(command.Parameters, "entity_type", entityType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("relations", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = relationNames });
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                AddStringParameter(command.Parameters, "subject_id", subjectsIds[0], 64);
                AddFixedCharParameter(command.Parameters, "snap_token", snapToken.Value, 26);

                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    _q.GetRelationsWithSingleSubjectMultiRelationSnap.Bind(command.Parameters);
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
                await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithMultiSubjectMultiRelationSnap.Sql);
                AddStringParameter(command.Parameters, "entity_type", entityType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("relations", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = relationNames });
                AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("subject_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = subjectsIds });
                AddFixedCharParameter(command.Parameters, "snap_token", snapToken.Value, 26);

                var multi = PooledList<RelationTuple>.Rent();
                try
                {
                    _q.GetRelationsWithMultiSubjectMultiRelationSnap.Bind(command.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsWithEntityIdsMultiRelationSnap.Sql);
            AddRelationsWithEntityIdsMultiRelationParameters(command.Parameters, entityType, relationNames, subjectType,
                entityIds, subjectRelation, snapToken);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                _q.GetRelationsWithEntityIdsMultiRelationSnap.Bind(command.Parameters);
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
            var statement = scope.HasValue ? _q.GetRelationsJoinedScoped : _q.GetRelationsJoined;
            await using var command = _hotPathDataSource.CreateCommand(statement.Sql);
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
                statement.Bind(command.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.GetRelationsJoinedByEntityIds.Sql);
            AddRelationsJoinedByEntityIdsParameters(command.Parameters, mainFilter.EntityType, mainFilter.Relation,
                entityIds, subEntityType, subRelation, mainFilter.SnapToken);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                _q.GetRelationsJoinedByEntityIds.Bind(command.Parameters);
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
            command.CommandText = _q.HasTupleToUserSetRelation.Sql;
            PopulateHasTupleToUserSetRelationCommand(command.Parameters, entityType, entityId, tupleSetRelation,
                subEntityType, computedRelation, subjectType, subjectId, snapToken);

            _q.HasTupleToUserSetRelation.Bind(command.Parameters);
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
            command.CommandText = _q.HasUsersetJoinRelation.Sql;
            PopulateHasUsersetJoinRelationCommand(command.Parameters, entityType, entityId, relation,
                computedRelation, subjectType, subjectId, snapToken);

            _q.HasUsersetJoinRelation.Bind(command.Parameters);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<bool> HasFusedExpression(string entityType, string entityId, IReadOnlyList<FusedCheckLeaf> leaves,
        bool requireAll, string? subjectType, string? subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.InternalSourceInstance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            var statement = FusedExpressionSql.BuildStatement(leaves, requireAll, _q.RelationsTable, _q.AttributesTable, batchResolvedSnapToken: false);
            await using var command = _hotPathDataSource.CreateCommand(statement.Sql);
            AddStringParameter(command.Parameters, "entity_type", entityType, 256);
            AddStringParameter(command.Parameters, "entity_id", entityId, 64);
            if (subjectType is not null) AddStringParameter(command.Parameters, "subject_type", subjectType, 256);
            if (subjectId is not null) AddStringParameter(command.Parameters, "subject_id", subjectId, 64);
            AddFixedCharParameter(command.Parameters, "snap_token", snapToken.Value, 26);
            for (var i = 0; i < leaves.Count; i++)
                FusedExpressionSql.WriteLeafParameters(command.Parameters, leaves[i], i);

            statement.Bind(command.Parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) && reader.GetBoolean(0);
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
            command.CommandText = _q.HasTrueBoolAttribute.Sql;
            PopulateHasTrueBoolAttributeCommand(command.Parameters, entityType, entityId, attribute, snapToken);
            _q.HasTrueBoolAttribute.Bind(command.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectAttributesByEntityAttributeFilter.Sql);
            AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
            AddStringParameter(command.Parameters, "attribute", filter.Attribute, 64);
            AddNullableStringParameter(command.Parameters, "entity_id", filter.EntityId, 64);
            AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);

            var rows = new List<AttributeTuple>();
            _q.SelectAttributesByEntityAttributeFilter.Bind(command.Parameters);
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
                await using var command = _hotPathDataSource.CreateCommand(_q.GetAttributesDictScoped.Sql);
                AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
                command.Parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = filter.Attributes });
                AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);
                AddStringParameter(command.Parameters, "scope_relation", s.Relation, 64);
                AddStringParameter(command.Parameters, "scope_subject_type", s.SubjectType, 256);
                AddStringParameter(command.Parameters, "scope_subject_id", s.SubjectId, 64);

                var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
                _q.GetAttributesDictScoped.Bind(command.Parameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var tuple = ReadAttributeTuple(reader);
                    dict[(tuple.Attribute, tuple.EntityId)] = tuple;
                }
                return dict;
            }

            await using var unscopedCommand = _hotPathDataSource.CreateCommand(_q.SelectAttributesByEntityAttributesFilter.Sql);
            AddNullableStringParameter(unscopedCommand.Parameters, "entity_id", filter.EntityId, 64);
            AddStringParameter(unscopedCommand.Parameters, "entity_type", filter.EntityType, 256);
            unscopedCommand.Parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = filter.Attributes });
            AddFixedCharParameter(unscopedCommand.Parameters, "snap_token", filter.SnapToken.Value, 26);

            var unscopedResult = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
            _q.SelectAttributesByEntityAttributesFilter.Bind(unscopedCommand.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectAttributesByEntityAttributesFilter.Sql);
            AddNullableStringParameter(command.Parameters, "entity_id", filter.EntityId, 64);
            AddStringParameter(command.Parameters, "entity_type", filter.EntityType, 256);
            command.Parameters.Add(new NpgsqlParameter<string[]>("attributes", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = filter.Attributes });
            AddFixedCharParameter(command.Parameters, "snap_token", filter.SnapToken.Value, 26);

            var pooled = PooledList<AttributeTuple>.Rent();
            try
            {
                _q.SelectAttributesByEntityAttributesFilter.Bind(command.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectAttributesWithEntityIdsByAttributeFilter.Sql);
            AddAttributeWithEntityIdsParameters(command.Parameters, filter.EntityType, filter.Attribute, entitiesIds,
                filter.SnapToken);

            var rows = new List<AttributeTuple>();
            _q.SelectAttributesWithEntityIdsByAttributeFilter.Bind(command.Parameters);
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
            await using var command = _hotPathDataSource.CreateCommand(_q.SelectAttributesWithEntityIdsByEntityAttributesFilter.Sql);
            AddAttributesWithEntityIdsParameters(command.Parameters, filter.EntityType, filter.Attributes, entitiesIds,
                filter.SnapToken);

            var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
            _q.SelectAttributesWithEntityIdsByEntityAttributesFilter.Bind(command.Parameters);
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
            cmd.CommandText = _q.GetEntityIdsExcluding.Sql;
            cmd.Parameters.Add(new NpgsqlParameter<string>("entity_type", NpgsqlDbType.Varchar) { TypedValue = entityType });
            cmd.Parameters.Add(new NpgsqlParameter<string>("snap_token", NpgsqlDbType.Char) { Size = 26, TypedValue = snapToken.Value });
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("exclude_ids", NpgsqlDbType.Array | NpgsqlDbType.Varchar) { TypedValue = excludeIds.ToArray() });
            _q.GetEntityIdsExcluding.Bind(cmd.Parameters);
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
            cmd.CommandText = _q.GetSubjectIdsExcluding.Sql;
            AddSubjectIdsExcludingParameters(cmd.Parameters, subjectType, excludeIds, snapToken);
            _q.GetSubjectIdsExcluding.Bind(cmd.Parameters);
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
