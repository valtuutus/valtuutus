using System.Collections.Concurrent;
using System.Data;
using System.Text.Json.Nodes;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data.SqlServer.Utils;
using Valtuutus.Data.Db;
using Microsoft.Data.SqlClient;

namespace Valtuutus.Data.SqlServer;

public class SqlServerDataReaderProvider : RateLimiterExecuter, IDataReaderProvider, IRelationalCheckOps
{
    // Internal (not private): FusedExpressionSql builds fused-expression fragments for both this
    // class's HasFusedExpression and SqlServerBatchOps.AddHasFusedExpressionToBatch, and needs the
    // exact same snap-token predicate text those queries use.
    internal const string SnapPredicate = "created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)";

    private readonly DbConnectionFactory _connectionFactory;

    private static readonly ConcurrentDictionary<DbQueryCacheKey, ReaderQueries> QueryCache = new();
    private readonly ReaderQueries _q;

    public SqlServerDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions) : base(options)
    {
        _connectionFactory = connectionFactory;
        _q = GetQueries(dbOptions);
    }

    // Internal so SqlServerBatchOps resolves the same cached ReaderQueries instance this provider
    // uses — the dialect catalog and the single-op path reference one set of SQL strings.
    internal static ReaderQueries GetQueries(IValtuutusDbOptions dbOptions) =>
        QueryCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildQueries(key));

    // Internal (not private): SqlServerBatchOps' dialect hooks construct byte-identical
    // SqlParameters (same SqlDbType, same size) through these exact helpers instead of
    // re-deriving them.
    internal static void AddStringParameter(SqlParameterCollection parameters, string name, string value, int size)
    {
        parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, size) { Value = value });
    }

    internal static void AddNullableStringParameter(SqlParameterCollection parameters, string name, string? value, int size)
    {
        parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, size)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value
        });
    }

    internal static void AddFixedCharParameter(SqlParameterCollection parameters, string name, string value, int size)
    {
        parameters.Add(new SqlParameter(name, SqlDbType.NChar, size) { Value = value });
    }

    private void AddTvpParameter(SqlParameterCollection parameters, string name, IEnumerable<string> values)
    {
        TvpHelper.AsTvpParameter(values, _q.TvpListIdsTypeName).AddParameter(parameters, name);
    }

    // Attribute-name lists come from the schema, so counts are small and bounded: inlined
    // @Attr0..@AttrN parameters keep plan-cache entries bounded by the schema.
    private static readonly ConcurrentDictionary<(string Prefix, int Count), string> AttributeInQueryCache = new();

    private static string GetAttributeInQuery(string prefix, int count) =>
        AttributeInQueryCache.GetOrAdd((prefix, count), static key =>
            $"{key.Prefix}({string.Join(", ", Enumerable.Range(0, key.Count).Select(i => $"@Attr{i}"))})");

    private static void AddAttributeInParameters(SqlParameterCollection parameters, string[] attributes)
    {
        for (var i = 0; i < attributes.Length; i++)
            AddStringParameter(parameters, $"@Attr{i}", attributes[i], 64);
    }

    private static RelationTuple ReadRelationTuple(SqlDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5));

    private static JsonValue ParseJsonValue(string rawJson)
    {
        return JsonNode.Parse(rawJson) as JsonValue
            ?? throw new InvalidOperationException("Attribute value must deserialize to a JsonValue.");
    }

    private static AttributeTuple ReadAttributeTuple(SqlDataReader reader)
    {
        return new AttributeTuple(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseJsonValue(reader.GetString(3)));
    }

    private SqlCommand CreateCommand(SqlConnection connection, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        return command;
    }

    private static ReaderQueries BuildQueries(DbQueryCacheKey key)
    {
        var relationsTable = $"[{key.Schema}].[{key.RelationsTable}]";
        var attributesTable = $"[{key.Schema}].[{key.AttributesTable}]";
        var snapPredicate = SnapPredicate;

        return new ReaderQueries
        {
            RelationsTable = relationsTable,
            AttributesTable = attributesTable,
            TvpListIdsTypeName = SqlBuilderExtensions.FormatTvpListIdsName(key.Schema),
            GetLatestSnapToken = string.Format(
                "SELECT TOP 1 id FROM [{0}].[{1}] ORDER BY id DESC", key.Schema, key.TransactionsTable),
            GetAttribute = $"""
                SELECT TOP 1 entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute = @Attribute
                """,
            GetAttributeWithEntityId = $"""
                SELECT TOP 1 entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute = @Attribute
                  AND entity_id = @EntityId
                """,
            HasTrueBoolAttribute = $"""
                SELECT CASE WHEN EXISTS(
                    SELECT 1 FROM {attributesTable}
                    WHERE {snapPredicate}
                      AND entity_type = @EntityType AND entity_id = @EntityId
                      AND attribute = @Attribute AND value = 'true'
                ) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END
                """,
            // Split instead of "(@EntityId IS NULL OR entity_id = @EntityId)": the catch-all
            // predicate blocks an index seek on entity_id.
            SelectAttributesByEntityAttributeFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute = @Attribute
                """,
            SelectAttributesByEntityAttributeFilterWithEntityId = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute = @Attribute
                  AND entity_id = @EntityId
                """,
            // Prefix queries: completed by GetAttributeInQuery, which appends an inlined
            // (@Attr0..@AttrN) list.
            SelectAttributesByEntityAttributesPrefix = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute IN
                """,
            SelectAttributesByEntityAttributesWithEntityIdPrefix = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND entity_id = @EntityId
                  AND attribute IN
                """,
            SelectAttributesWithEntityIdsByAttributeFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute = @Attribute
                  AND entity_id IN (SELECT id FROM @EntityIds)
                """,
            SelectAttributesWithEntityIdsByEntityAttributesPrefix = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND entity_id IN (SELECT id FROM @EntityIds)
                  AND attribute IN
                """,
            SelectRelationsByTupleFilter = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND entity_id = @EntityId
                  AND relation = @Relation
                  AND (@SubjectId IS NULL OR subject_id = @SubjectId)
                  AND (@SubjectRelation IS NULL OR subject_relation = @SubjectRelation)
                  AND (@SubjectType IS NULL OR subject_type = @SubjectType)
                """,
            SelectRelationsByTupleFilterNoSubject = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND entity_id = @EntityId
                  AND relation = @Relation
                """,
            HasDirectRelation = $"""
                SELECT CASE WHEN EXISTS(
                    SELECT 1 FROM {relationsTable}
                    WHERE {snapPredicate}
                      AND entity_type = @EntityType AND entity_id = @EntityId AND relation = @Relation
                      AND subject_id = @SubjectId AND subject_relation = ''
                ) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END
                """,
            GetIndirectRelations = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType AND entity_id = @EntityId AND relation = @Relation
                  AND subject_relation <> ''
                """,
            HasAnyDirectRelation = $"""
                SELECT CASE WHEN EXISTS(
                    SELECT 1 FROM {relationsTable}
                    WHERE {snapPredicate}
                      AND entity_type = @EntityType AND entity_id IN (SELECT id FROM @EntityIds) AND relation = @Relation
                      AND subject_id = @SubjectId AND subject_relation = ''
                ) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END
                """,
            HasAnyOfDirectRelations = $"""
                SELECT DISTINCT relation FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType AND entity_id = @EntityId AND relation IN (SELECT id FROM @Relations)
                  AND subject_id = @SubjectId AND subject_relation = ''
                """,
            HasAnyOfAttributes = $"""
                SELECT DISTINCT attribute FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType AND entity_id = @EntityId AND attribute IN (SELECT id FROM @Attributes)
                  AND value = 'true'
                """,
            SelectRelationsWithEntityIds = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND (@SubjectType IS NULL OR subject_type = @SubjectType)
                  AND (@EntityType IS NULL OR entity_type = @EntityType)
                  AND (@Relation IS NULL OR relation = @Relation)
                  AND entity_id IN (SELECT id FROM @EntityIds)
                  AND (@SubjectRelation IS NULL OR subject_relation = @SubjectRelation)
                OPTION (RECOMPILE)
                """,
            GetRelationsWithSingleSubjectSnap =
                $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken) AND entity_type = @EntityType AND relation = @Relation AND subject_type = @SubjectType AND subject_id = @SubjectId",
            GetRelationsWithMultiSubjectSnap = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND (@EntityType IS NULL OR entity_type = @EntityType)
                  AND (@Relation IS NULL OR relation = @Relation)
                  AND subject_type = @SubjectType
                  AND subject_id IN (SELECT id FROM @SubjectIds)
                """,
            GetRelationsWithSingleSubjectMultiRelationSnap =
                $"SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation FROM {relationsTable} WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken) AND entity_type = @EntityType AND relation IN (SELECT id FROM @Relations) AND subject_type = @SubjectType AND subject_id = @SubjectId",
            GetRelationsWithMultiSubjectMultiRelationSnap = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND relation IN (SELECT id FROM @Relations)
                  AND subject_type = @SubjectType
                  AND subject_id IN (SELECT id FROM @SubjectIds)
                """,
            GetRelationsWithEntityIdsMultiRelationSnap = $"""
                SELECT entity_type, entity_id, relation, subject_type, subject_id, subject_relation
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND relation IN (SELECT id FROM @Relations)
                  AND subject_type = @SubjectType
                  AND entity_id IN (SELECT id FROM @EntityIds)
                  AND (@SubjectRelation IS NULL OR subject_relation = @SubjectRelation)
                OPTION (RECOMPILE)
                """,
            GetRelationsJoined = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} AS r_main
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation = @Relation
                  AND r_main.subject_type = @SubEntityType
                  AND r_main.subject_id IN (
                      SELECT entity_id FROM {relationsTable}
                      WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)
                        AND entity_type = @SubEntityType
                        AND relation = @SubRelation
                        AND subject_type = @SubjectType
                        AND subject_id = @SubjectId
                  )
                """,
            GetRelationsJoinedByEntityIds = $"""
                SELECT r_dep.entity_type, r_dep.entity_id, r_dep.relation, r_dep.subject_type, r_dep.subject_id, r_dep.subject_relation
                FROM {relationsTable} AS r_dep
                WHERE r_dep.created_tx_id <= @SnapToken AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > @SnapToken)
                  AND r_dep.entity_type = @SubEntityType
                  AND r_dep.relation = @SubRelation
                  AND r_dep.entity_id IN (
                      SELECT subject_id FROM {relationsTable}
                      WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)
                        AND entity_type = @EntityType
                        AND entity_id IN (SELECT id FROM @EntityIds)
                        AND relation = @Relation
                        AND subject_type = @SubEntityType
                  )
                """,
            HasTupleToUserSetRelation = $"""
                SELECT CASE WHEN EXISTS(
                    SELECT 1 FROM {relationsTable} r_main
                    INNER JOIN {relationsTable} r_dep
                        ON r_dep.entity_type = r_main.subject_type AND r_dep.entity_id = r_main.subject_id
                    WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                      AND r_main.entity_type = @EntityType
                      AND r_main.entity_id = @EntityId
                      AND r_main.relation = @TupleSetRelation
                      AND r_main.subject_relation = ''
                      AND r_dep.created_tx_id <= @SnapToken AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > @SnapToken)
                      AND r_dep.relation = @ComputedRelation
                      AND r_dep.subject_type = @SubjectType
                      AND r_dep.subject_id = @SubjectId
                      AND r_dep.subject_relation = ''
                ) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END
                """,
            HasUsersetJoinRelation = $"""
                SELECT CASE WHEN (
                    EXISTS(
                        SELECT 1 FROM {relationsTable}
                        WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)
                          AND entity_type = @EntityType
                          AND entity_id = @EntityId
                          AND relation = @Relation
                          AND subject_id = @SubjectId
                          AND subject_relation = ''
                    )
                    OR EXISTS(
                        SELECT 1 FROM {relationsTable} r_main
                        INNER JOIN {relationsTable} r_dep
                            ON r_dep.entity_type = r_main.subject_type AND r_dep.entity_id = r_main.subject_id
                        WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                          AND r_main.entity_type = @EntityType
                          AND r_main.entity_id = @EntityId
                          AND r_main.relation = @Relation
                          AND r_main.subject_relation = @ComputedRelation
                          AND r_dep.created_tx_id <= @SnapToken AND (r_dep.deleted_tx_id IS NULL OR r_dep.deleted_tx_id > @SnapToken)
                          AND r_dep.relation = @ComputedRelation
                          AND r_dep.subject_type = @SubjectType
                          AND r_dep.subject_id = @SubjectId
                          AND r_dep.subject_relation = ''
                    )
                ) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END
                """,
            GetRelationsWithSingleSubjectScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation = @Relation
                  AND r_main.subject_type = @SubjectType
                  AND r_main.subject_id = @SubjectId
                """,
            GetRelationsWithMultiSubjectScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation = @Relation
                  AND r_main.subject_type = @SubjectType
                  AND r_main.subject_id IN (SELECT id FROM @SubjectIds)
                """,
            GetRelationsWithSingleSubjectMultiRelationScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation IN (SELECT id FROM @Relations)
                  AND r_main.subject_type = @SubjectType
                  AND r_main.subject_id = @SubjectId
                """,
            GetRelationsWithMultiSubjectMultiRelationScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation IN (SELECT id FROM @Relations)
                  AND r_main.subject_type = @SubjectType
                  AND r_main.subject_id IN (SELECT id FROM @SubjectIds)
                """,
            GetRelationsJoinedScoped = $"""
                SELECT r_main.entity_type, r_main.entity_id, r_main.relation, r_main.subject_type, r_main.subject_id, r_main.subject_relation
                FROM {relationsTable} AS r_main
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = r_main.entity_type
                    AND r_scope.entity_id = r_main.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE r_main.created_tx_id <= @SnapToken AND (r_main.deleted_tx_id IS NULL OR r_main.deleted_tx_id > @SnapToken)
                  AND r_main.entity_type = @EntityType
                  AND r_main.relation = @Relation
                  AND r_main.subject_type = @SubEntityType
                  AND r_main.subject_id IN (
                      SELECT entity_id FROM {relationsTable}
                      WHERE created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)
                        AND entity_type = @SubEntityType
                        AND relation = @SubRelation
                        AND subject_type = @SubjectType
                        AND subject_id = @SubjectId
                  )
                """,
            GetAttributesDictScopedPrefix = $"""
                SELECT a.entity_type, a.entity_id, a.attribute, a.value
                FROM {attributesTable} a
                INNER JOIN {relationsTable} r_scope
                    ON r_scope.entity_type = a.entity_type
                    AND r_scope.entity_id = a.entity_id
                    AND r_scope.relation = @ScopeRelation
                    AND r_scope.subject_type = @ScopeSubjectType
                    AND r_scope.subject_id = @ScopeSubjectId
                    AND r_scope.created_tx_id <= @SnapToken AND (r_scope.deleted_tx_id IS NULL OR r_scope.deleted_tx_id > @SnapToken)
                WHERE a.created_tx_id <= @SnapToken AND (a.deleted_tx_id IS NULL OR a.deleted_tx_id > @SnapToken)
                  AND a.entity_type = @EntityType
                  AND a.attribute IN
                """,
            GetEntityIdsExcluding = $"""
                SELECT DISTINCT entity_id
                FROM (
                    SELECT entity_id FROM {relationsTable}
                    WHERE {snapPredicate} AND entity_type = @EntityType
                    UNION
                    SELECT entity_id FROM {attributesTable}
                    WHERE {snapPredicate} AND entity_type = @EntityType
                ) universe
                WHERE entity_id NOT IN (SELECT id FROM @ExcludeIds)
                """,
            GetSubjectIdsExcluding = $"""
                SELECT DISTINCT subject_id
                FROM {relationsTable}
                WHERE {snapPredicate}
                  AND subject_type = @SubjectType
                  AND subject_relation = ''
                  AND subject_id NOT IN (SELECT id FROM @ExcludeIds)
                """,
        };
    }

    // Internal (not private): SqlServerBatchOps' dialect catalog serves the exact same SQL text
    // as these single-op queries — one definition, shared through GetQueries, so the two paths
    // can never drift apart.
    internal sealed record ReaderQueries
    {
        public required string RelationsTable { get; init; }
        public required string AttributesTable { get; init; }
        public required string TvpListIdsTypeName { get; init; }
        public required string GetLatestSnapToken { get; init; }
        public required string GetAttribute { get; init; }
        public required string GetAttributeWithEntityId { get; init; }
        public required string HasTrueBoolAttribute { get; init; }
        public required string HasAnyOfAttributes { get; init; }
        public required string SelectAttributesByEntityAttributeFilter { get; init; }
        public required string SelectAttributesByEntityAttributeFilterWithEntityId { get; init; }
        public required string SelectAttributesByEntityAttributesPrefix { get; init; }
        public required string SelectAttributesByEntityAttributesWithEntityIdPrefix { get; init; }
        public required string SelectAttributesWithEntityIdsByAttributeFilter { get; init; }
        public required string SelectAttributesWithEntityIdsByEntityAttributesPrefix { get; init; }
        public required string SelectRelationsByTupleFilter { get; init; }
        public required string SelectRelationsByTupleFilterNoSubject { get; init; }
        public required string HasDirectRelation { get; init; }
        public required string GetIndirectRelations { get; init; }
        public required string HasAnyDirectRelation { get; init; }
        public required string HasAnyOfDirectRelations { get; init; }
        public required string SelectRelationsWithEntityIds { get; init; }
        public required string GetRelationsWithSingleSubjectSnap { get; init; }
        public required string GetRelationsWithMultiSubjectSnap { get; init; }
        public required string GetRelationsWithSingleSubjectMultiRelationSnap { get; init; }
        public required string GetRelationsWithMultiSubjectMultiRelationSnap { get; init; }
        public required string GetRelationsWithEntityIdsMultiRelationSnap { get; init; }
        public required string GetRelationsJoined { get; init; }
        public required string GetRelationsJoinedByEntityIds { get; init; }
        public required string HasTupleToUserSetRelation { get; init; }
        public required string HasUsersetJoinRelation { get; init; }
        public required string GetRelationsWithSingleSubjectScoped { get; init; }
        public required string GetRelationsWithMultiSubjectScoped { get; init; }
        public required string GetRelationsWithSingleSubjectMultiRelationScoped { get; init; }
        public required string GetRelationsWithMultiSubjectMultiRelationScoped { get; init; }
        public required string GetRelationsJoinedScoped { get; init; }
        public required string GetAttributesDictScopedPrefix { get; init; }
        public required string GetEntityIdsExcluding { get; init; }
        public required string GetSubjectIdsExcluding { get; init; }
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);
            var hasEntityId = !string.IsNullOrWhiteSpace(filter.EntityId);

            await using var command = CreateCommand(connection, hasEntityId ? _q.GetAttributeWithEntityId : _q.GetAttribute);
            AddStringParameter(command.Parameters, "@EntityType", filter.EntityType, 256);
            AddStringParameter(command.Parameters, "@Attribute", filter.Attribute, 64);
            if (hasEntityId)
                AddStringParameter(command.Parameters, "@EntityId", filter.EntityId!, 64);
            AddFixedCharParameter(command.Parameters, "@SnapToken", filter.SnapToken.Value, 26);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow | CommandBehavior.SequentialAccess, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;

            return ReadAttributeTuple(reader);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private static void PopulateHasTrueBoolAttributeCommand(SqlParameterCollection parameters, string entityType,
        string entityId, string attribute, SnapToken snapToken)
    {
        AddStringParameter(parameters, "@EntityType", entityType, 256);
        AddStringParameter(parameters, "@EntityId", entityId, 64);
        AddStringParameter(parameters, "@Attribute", attribute, 64);
        AddFixedCharParameter(parameters, "@SnapToken", snapToken.Value, 26);
    }

    public async Task<bool> HasTrueBoolAttribute(string entityType, string entityId, string attribute,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);
            await using var command = CreateCommand(connection, _q.HasTrueBoolAttribute);
            PopulateHasTrueBoolAttributeCommand(command.Parameters, entityType, entityId, attribute, snapToken);
            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private void PopulateHasAnyOfAttributesCommand(SqlParameterCollection parameters, string entityType,
        string entityId, string[] attributeNames, SnapToken snapToken)
    {
        AddStringParameter(parameters, "@EntityType", entityType, 256);
        AddStringParameter(parameters, "@EntityId", entityId, 64);
        AddTvpParameter(parameters, "@Attributes", attributeNames);
        AddFixedCharParameter(parameters, "@SnapToken", snapToken.Value, 26);
    }

    public async Task<HashSet<string>> HasAnyOfAttributes(string entityType, string entityId, string[] attributeNames,
        SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.HasAnyOfAttributes);
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

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            var hasEntityId = !string.IsNullOrWhiteSpace(filter.EntityId);
            await using var command = CreateCommand(connection,
                hasEntityId ? _q.SelectAttributesByEntityAttributeFilterWithEntityId : _q.SelectAttributesByEntityAttributeFilter);
            AddStringParameter(command.Parameters, "@EntityType", filter.EntityType, 256);
            AddStringParameter(command.Parameters, "@Attribute", filter.Attribute, 64);
            if (hasEntityId)
                AddStringParameter(command.Parameters, "@EntityId", filter.EntityId!, 64);
            AddFixedCharParameter(command.Parameters, "@SnapToken", filter.SnapToken.Value, 26);

            var rows = new List<AttributeTuple>();
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            if (scope is { } s)
            {
                await using var command = CreateCommand(connection,
                    GetAttributeInQuery(_q.GetAttributesDictScopedPrefix, filter.Attributes.Length));
                AddStringParameter(command.Parameters, "@EntityType", filter.EntityType, 256);
                AddAttributeInParameters(command.Parameters, filter.Attributes);
                AddFixedCharParameter(command.Parameters, "@SnapToken", filter.SnapToken.Value, 26);
                AddStringParameter(command.Parameters, "@ScopeRelation", s.Relation, 64);
                AddStringParameter(command.Parameters, "@ScopeSubjectType", s.SubjectType, 256);
                AddStringParameter(command.Parameters, "@ScopeSubjectId", s.SubjectId, 64);

                var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var tuple = ReadAttributeTuple(reader);
                    dict[(tuple.Attribute, tuple.EntityId)] = tuple;
                }
                return dict;
            }

            var hasEntityId = !string.IsNullOrWhiteSpace(filter.EntityId);
            await using var unscopedCommand = CreateCommand(connection, GetAttributeInQuery(
                hasEntityId ? _q.SelectAttributesByEntityAttributesWithEntityIdPrefix : _q.SelectAttributesByEntityAttributesPrefix,
                filter.Attributes.Length));
            if (hasEntityId)
                AddStringParameter(unscopedCommand.Parameters, "@EntityId", filter.EntityId!, 64);
            AddStringParameter(unscopedCommand.Parameters, "@EntityType", filter.EntityType, 256);
            AddAttributeInParameters(unscopedCommand.Parameters, filter.Attributes);
            AddFixedCharParameter(unscopedCommand.Parameters, "@SnapToken", filter.SnapToken.Value, 26);

            var unscopedResult = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
            await using var unscopedReader = await unscopedCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            var hasEntityId = !string.IsNullOrWhiteSpace(filter.EntityId);
            await using var command = CreateCommand(connection, GetAttributeInQuery(
                hasEntityId ? _q.SelectAttributesByEntityAttributesWithEntityIdPrefix : _q.SelectAttributesByEntityAttributesPrefix,
                filter.Attributes.Length));
            if (hasEntityId)
                AddStringParameter(command.Parameters, "@EntityId", filter.EntityId!, 64);
            AddStringParameter(command.Parameters, "@EntityType", filter.EntityType, 256);
            AddAttributeInParameters(command.Parameters, filter.Attributes);
            AddFixedCharParameter(command.Parameters, "@SnapToken", filter.SnapToken.Value, 26);

            var pooled = PooledList<AttributeTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.SelectAttributesWithEntityIdsByAttributeFilter);
            AddStringParameter(command.Parameters, "@EntityType", filter.EntityType, 256);
            AddStringParameter(command.Parameters, "@Attribute", filter.Attribute, 64);
            AddTvpParameter(command.Parameters, "@EntityIds", entitiesIds);
            AddFixedCharParameter(command.Parameters, "@SnapToken", filter.SnapToken.Value, 26);

            var rows = new List<AttributeTuple>();
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection,
                GetAttributeInQuery(_q.SelectAttributesWithEntityIdsByEntityAttributesPrefix, filter.Attributes.Length));
            AddStringParameter(command.Parameters, "@EntityType", filter.EntityType, 256);
            AddAttributeInParameters(command.Parameters, filter.Attributes);
            AddTvpParameter(command.Parameters, "@EntityIds", entitiesIds);
            AddFixedCharParameter(command.Parameters, "@SnapToken", filter.SnapToken.Value, 26);

            var dict = new Dictionary<(string AttributeName, string EntityId), AttributeTuple>();
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.GetLatestSnapToken);
            var res = await command.ExecuteScalarAsync(cancellationToken) as string;
            return res != null ? new SnapToken(res) : (SnapToken?)null;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private string PopulateGetRelationsCommand(SqlParameterCollection parameters, RelationTupleFilter tupleFilter)
    {
        var noSubjectFilters = string.IsNullOrWhiteSpace(tupleFilter.SubjectId)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectRelation)
            && string.IsNullOrWhiteSpace(tupleFilter.SubjectType);

        AddStringParameter(parameters, "@EntityType", tupleFilter.EntityType, 256);
        AddStringParameter(parameters, "@EntityId", tupleFilter.EntityId, 64);
        AddStringParameter(parameters, "@Relation", tupleFilter.Relation, 64);
        if (!noSubjectFilters)
        {
            AddNullableStringParameter(parameters, "@SubjectId", tupleFilter.SubjectId, 64);
            AddNullableStringParameter(parameters, "@SubjectRelation", tupleFilter.SubjectRelation, 64);
            AddNullableStringParameter(parameters, "@SubjectType", tupleFilter.SubjectType, 256);
        }
        AddFixedCharParameter(parameters, "@SnapToken", tupleFilter.SnapToken.Value, 26);

        return noSubjectFilters ? _q.SelectRelationsByTupleFilterNoSubject : _q.SelectRelationsByTupleFilter;
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, string.Empty);
            command.CommandText = PopulateGetRelationsCommand(command.Parameters, tupleFilter);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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

    private static void PopulateHasDirectRelationCommand(SqlParameterCollection parameters, RelationTupleFilter tupleFilter, string subjectId)
    {
        AddStringParameter(parameters, "@EntityType", tupleFilter.EntityType, 256);
        AddStringParameter(parameters, "@EntityId", tupleFilter.EntityId, 64);
        AddStringParameter(parameters, "@Relation", tupleFilter.Relation, 64);
        AddStringParameter(parameters, "@SubjectId", subjectId, 64);
        AddFixedCharParameter(parameters, "@SnapToken", tupleFilter.SnapToken.Value, 26);
    }

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.HasDirectRelation);
            PopulateHasDirectRelationCommand(command.Parameters, tupleFilter, subjectId);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.GetIndirectRelations);
            PopulateGetIndirectRelationsCommand(command.Parameters, tupleFilter);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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

    private static void PopulateGetIndirectRelationsCommand(SqlParameterCollection parameters, RelationTupleFilter tupleFilter)
    {
        AddStringParameter(parameters, "@EntityType", tupleFilter.EntityType, 256);
        AddStringParameter(parameters, "@EntityId", tupleFilter.EntityId, 64);
        AddStringParameter(parameters, "@Relation", tupleFilter.Relation, 64);
        AddFixedCharParameter(parameters, "@SnapToken", tupleFilter.SnapToken.Value, 26);
    }

    private void PopulateHasAnyDirectRelationCommand(SqlParameterCollection parameters, string entityType,
        string[] entityIds, string relation, string subjectId, SnapToken snapToken)
    {
        AddStringParameter(parameters, "@EntityType", entityType, 256);
        AddTvpParameter(parameters, "@EntityIds", entityIds);
        AddStringParameter(parameters, "@Relation", relation, 64);
        AddStringParameter(parameters, "@SubjectId", subjectId, 64);
        AddFixedCharParameter(parameters, "@SnapToken", snapToken.Value, 26);
    }

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.HasAnyDirectRelation);
            PopulateHasAnyDirectRelationCommand(command.Parameters, entityType, entityIds, relation, subjectId, snapToken);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private void PopulateHasAnyOfDirectRelationsCommand(SqlParameterCollection parameters, string entityType,
        string entityId, string[] relationNames, string subjectId, SnapToken snapToken)
    {
        AddStringParameter(parameters, "@EntityType", entityType, 256);
        AddStringParameter(parameters, "@EntityId", entityId, 64);
        AddTvpParameter(parameters, "@Relations", relationNames);
        AddStringParameter(parameters, "@SubjectId", subjectId, 64);
        AddFixedCharParameter(parameters, "@SnapToken", snapToken.Value, 26);
    }

    public async Task<HashSet<string>> HasAnyOfDirectRelations(string entityType, string entityId, string[] relationNames,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.HasAnyOfDirectRelations);
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

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.SelectRelationsWithEntityIds);
            AddNullableStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
            AddNullableStringParameter(command.Parameters, "@EntityType", entityRelationFilter.EntityType, 256);
            AddNullableStringParameter(command.Parameters, "@Relation", entityRelationFilter.Relation, 64);
            AddTvpParameter(command.Parameters, "@EntityIds", entityIds);
            AddNullableStringParameter(command.Parameters, "@SubjectRelation", subjectRelation, 64);
            AddFixedCharParameter(command.Parameters, "@SnapToken", entityRelationFilter.SnapToken.Value, 26);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            if (subjectsIds.Length == 1)
            {
                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    if (scope is { } s)
                    {
                        await using var command = CreateCommand(connection, _q.GetRelationsWithSingleSubjectScoped);
                        AddStringParameter(command.Parameters, "@EntityType", entityFilter.EntityType, 256);
                        AddStringParameter(command.Parameters, "@Relation", entityFilter.Relation, 64);
                        AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                        AddStringParameter(command.Parameters, "@SubjectId", subjectsIds[0], 64);
                        AddFixedCharParameter(command.Parameters, "@SnapToken", entityFilter.SnapToken.Value, 26);
                        AddStringParameter(command.Parameters, "@ScopeRelation", s.Relation, 64);
                        AddStringParameter(command.Parameters, "@ScopeSubjectType", s.SubjectType, 256);
                        AddStringParameter(command.Parameters, "@ScopeSubjectId", s.SubjectId, 64);

                        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                            pooled.Add(ReadRelationTuple(reader));
                    }
                    else
                    {
                        await using var command = CreateCommand(connection, _q.GetRelationsWithSingleSubjectSnap);
                        AddStringParameter(command.Parameters, "@EntityType", entityFilter.EntityType, 256);
                        AddStringParameter(command.Parameters, "@Relation", entityFilter.Relation, 64);
                        AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                        AddStringParameter(command.Parameters, "@SubjectId", subjectsIds[0], 64);
                        AddFixedCharParameter(command.Parameters, "@SnapToken", entityFilter.SnapToken.Value, 26);

                        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                            pooled.Add(ReadRelationTuple(reader));
                    }
                    return pooled;
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
            }

            var multi = PooledList<RelationTuple>.Rent();
            try
            {
                if (scope is { } ms)
                {
                    await using var command = CreateCommand(connection, _q.GetRelationsWithMultiSubjectScoped);
                    AddStringParameter(command.Parameters, "@EntityType", entityFilter.EntityType, 256);
                    AddStringParameter(command.Parameters, "@Relation", entityFilter.Relation, 64);
                    AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                    AddTvpParameter(command.Parameters, "@SubjectIds", subjectsIds);
                    AddFixedCharParameter(command.Parameters, "@SnapToken", entityFilter.SnapToken.Value, 26);
                    AddStringParameter(command.Parameters, "@ScopeRelation", ms.Relation, 64);
                    AddStringParameter(command.Parameters, "@ScopeSubjectType", ms.SubjectType, 256);
                    AddStringParameter(command.Parameters, "@ScopeSubjectId", ms.SubjectId, 64);

                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        multi.Add(ReadRelationTuple(reader));
                }
                else
                {
                    await using var command = CreateCommand(connection, _q.GetRelationsWithMultiSubjectSnap);
                    AddStringParameter(command.Parameters, "@EntityType", entityFilter.EntityType, 256);
                    AddStringParameter(command.Parameters, "@Relation", entityFilter.Relation, 64);
                    AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                    AddTvpParameter(command.Parameters, "@SubjectIds", subjectsIds);
                    AddFixedCharParameter(command.Parameters, "@SnapToken", entityFilter.SnapToken.Value, 26);

                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        multi.Add(ReadRelationTuple(reader));
                }
                return multi;
            }
            catch
            {
                multi.Dispose();
                throw;
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            if (subjectsIds.Length == 1)
            {
                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    if (scope is { } s)
                    {
                        await using var command = CreateCommand(connection, _q.GetRelationsWithSingleSubjectMultiRelationScoped);
                        AddStringParameter(command.Parameters, "@EntityType", entityType, 256);
                        AddTvpParameter(command.Parameters, "@Relations", relationNames);
                        AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                        AddStringParameter(command.Parameters, "@SubjectId", subjectsIds[0], 64);
                        AddFixedCharParameter(command.Parameters, "@SnapToken", snapToken.Value, 26);
                        AddStringParameter(command.Parameters, "@ScopeRelation", s.Relation, 64);
                        AddStringParameter(command.Parameters, "@ScopeSubjectType", s.SubjectType, 256);
                        AddStringParameter(command.Parameters, "@ScopeSubjectId", s.SubjectId, 64);

                        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                            pooled.Add(ReadRelationTuple(reader));
                    }
                    else
                    {
                        await using var command = CreateCommand(connection, _q.GetRelationsWithSingleSubjectMultiRelationSnap);
                        AddStringParameter(command.Parameters, "@EntityType", entityType, 256);
                        AddTvpParameter(command.Parameters, "@Relations", relationNames);
                        AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                        AddStringParameter(command.Parameters, "@SubjectId", subjectsIds[0], 64);
                        AddFixedCharParameter(command.Parameters, "@SnapToken", snapToken.Value, 26);

                        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                            pooled.Add(ReadRelationTuple(reader));
                    }
                    return pooled;
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
            }

            var multi = PooledList<RelationTuple>.Rent();
            try
            {
                if (scope is { } ms)
                {
                    await using var command = CreateCommand(connection, _q.GetRelationsWithMultiSubjectMultiRelationScoped);
                    AddStringParameter(command.Parameters, "@EntityType", entityType, 256);
                    AddTvpParameter(command.Parameters, "@Relations", relationNames);
                    AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                    AddTvpParameter(command.Parameters, "@SubjectIds", subjectsIds);
                    AddFixedCharParameter(command.Parameters, "@SnapToken", snapToken.Value, 26);
                    AddStringParameter(command.Parameters, "@ScopeRelation", ms.Relation, 64);
                    AddStringParameter(command.Parameters, "@ScopeSubjectType", ms.SubjectType, 256);
                    AddStringParameter(command.Parameters, "@ScopeSubjectId", ms.SubjectId, 64);

                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        multi.Add(ReadRelationTuple(reader));
                }
                else
                {
                    await using var command = CreateCommand(connection, _q.GetRelationsWithMultiSubjectMultiRelationSnap);
                    AddStringParameter(command.Parameters, "@EntityType", entityType, 256);
                    AddTvpParameter(command.Parameters, "@Relations", relationNames);
                    AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                    AddTvpParameter(command.Parameters, "@SubjectIds", subjectsIds);
                    AddFixedCharParameter(command.Parameters, "@SnapToken", snapToken.Value, 26);

                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        multi.Add(ReadRelationTuple(reader));
                }
                return multi;
            }
            catch
            {
                multi.Dispose();
                throw;
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.GetRelationsWithEntityIdsMultiRelationSnap);
            AddStringParameter(command.Parameters, "@EntityType", entityType, 256);
            AddTvpParameter(command.Parameters, "@Relations", relationNames);
            AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
            AddTvpParameter(command.Parameters, "@EntityIds", entityIds);
            AddNullableStringParameter(command.Parameters, "@SubjectRelation", subjectRelation, 64);
            AddFixedCharParameter(command.Parameters, "@SnapToken", snapToken.Value, 26);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                if (scope is { } s)
                {
                    await using var command = CreateCommand(connection, _q.GetRelationsJoinedScoped);
                    AddStringParameter(command.Parameters, "@EntityType", mainFilter.EntityType, 256);
                    AddStringParameter(command.Parameters, "@Relation", mainFilter.Relation, 64);
                    AddStringParameter(command.Parameters, "@SubEntityType", subEntityType, 256);
                    AddStringParameter(command.Parameters, "@SubRelation", subRelation, 64);
                    AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                    AddStringParameter(command.Parameters, "@SubjectId", subjectId, 64);
                    AddFixedCharParameter(command.Parameters, "@SnapToken", mainFilter.SnapToken.Value, 26);
                    AddStringParameter(command.Parameters, "@ScopeRelation", s.Relation, 64);
                    AddStringParameter(command.Parameters, "@ScopeSubjectType", s.SubjectType, 256);
                    AddStringParameter(command.Parameters, "@ScopeSubjectId", s.SubjectId, 64);

                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        pooled.Add(ReadRelationTuple(reader));
                }
                else
                {
                    await using var command = CreateCommand(connection, _q.GetRelationsJoined);
                    AddStringParameter(command.Parameters, "@EntityType", mainFilter.EntityType, 256);
                    AddStringParameter(command.Parameters, "@Relation", mainFilter.Relation, 64);
                    AddStringParameter(command.Parameters, "@SubEntityType", subEntityType, 256);
                    AddStringParameter(command.Parameters, "@SubRelation", subRelation, 64);
                    AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
                    AddStringParameter(command.Parameters, "@SubjectId", subjectId, 64);
                    AddFixedCharParameter(command.Parameters, "@SnapToken", mainFilter.SnapToken.Value, 26);

                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        pooled.Add(ReadRelationTuple(reader));
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                await using var command = CreateCommand(connection, _q.GetRelationsJoinedByEntityIds);
                AddStringParameter(command.Parameters, "@EntityType", mainFilter.EntityType, 256);
                AddStringParameter(command.Parameters, "@Relation", mainFilter.Relation, 64);
                AddTvpParameter(command.Parameters, "@EntityIds", entityIds);
                AddStringParameter(command.Parameters, "@SubEntityType", subEntityType, 256);
                AddStringParameter(command.Parameters, "@SubRelation", subRelation, 64);
                AddFixedCharParameter(command.Parameters, "@SnapToken", mainFilter.SnapToken.Value, 26);

                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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

    private static void PopulateHasTupleToUserSetRelationCommand(SqlParameterCollection parameters,
        string entityType, string entityId, string tupleSetRelation, string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken)
    {
        AddStringParameter(parameters, "@EntityType", entityType, 256);
        AddStringParameter(parameters, "@EntityId", entityId, 64);
        AddStringParameter(parameters, "@TupleSetRelation", tupleSetRelation, 64);
        AddStringParameter(parameters, "@ComputedRelation", computedRelation, 64);
        AddStringParameter(parameters, "@SubjectType", subjectType, 256);
        AddStringParameter(parameters, "@SubjectId", subjectId, 64);
        AddFixedCharParameter(parameters, "@SnapToken", snapToken.Value, 26);
    }

    public async Task<bool> HasTupleToUserSetRelation(
        string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.HasTupleToUserSetRelation);
            PopulateHasTupleToUserSetRelationCommand(command.Parameters, entityType, entityId, tupleSetRelation,
                subEntityType, computedRelation, subjectType, subjectId, snapToken);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private static void PopulateHasUsersetJoinRelationCommand(SqlParameterCollection parameters,
        string entityType, string entityId, string relation, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken)
    {
        AddStringParameter(parameters, "@EntityType", entityType, 256);
        AddStringParameter(parameters, "@EntityId", entityId, 64);
        AddStringParameter(parameters, "@Relation", relation, 64);
        AddStringParameter(parameters, "@ComputedRelation", computedRelation, 64);
        AddStringParameter(parameters, "@SubjectType", subjectType, 256);
        AddStringParameter(parameters, "@SubjectId", subjectId, 64);
        AddFixedCharParameter(parameters, "@SnapToken", snapToken.Value, 26);
    }

    public async Task<bool> HasUsersetJoinRelation(
        string entityType, string entityId, string relation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.HasUsersetJoinRelation);
            PopulateHasUsersetJoinRelationCommand(command.Parameters, entityType, entityId, relation,
                computedRelation, subjectType, subjectId, snapToken);

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
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await EnterQuery(cancellationToken);
        try
        {
            var sql = FusedExpressionSql.BuildCommandSql(leaves, requireAll, _q.RelationsTable, _q.AttributesTable);
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, sql);
            AddStringParameter(command.Parameters, "@EntityType", entityType, 256);
            AddStringParameter(command.Parameters, "@EntityId", entityId, 64);
            if (subjectType is not null) AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
            if (subjectId is not null) AddStringParameter(command.Parameters, "@SubjectId", subjectId, 64);
            AddFixedCharParameter(command.Parameters, "@SnapToken", snapToken.Value, 26);
            for (var i = 0; i < leaves.Count; i++)
                FusedExpressionSql.WriteLeafParameters(command.Parameters, leaves[i], i, _q.TvpListIdsTypeName);

            return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.GetEntityIdsExcluding);
            AddStringParameter(command.Parameters, "@EntityType", entityType, 256);
            AddFixedCharParameter(command.Parameters, "@SnapToken", snapToken.Value, 26);
            AddTvpParameter(command.Parameters, "@ExcludeIds", excludeIds);

            var result = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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
            await using var connection = (SqlConnection)_connectionFactory();
            await connection.OpenAsync(cancellationToken);

            await using var command = CreateCommand(connection, _q.GetSubjectIdsExcluding);
            AddStringParameter(command.Parameters, "@SubjectType", subjectType, 256);
            AddFixedCharParameter(command.Parameters, "@SnapToken", snapToken.Value, 26);
            AddTvpParameter(command.Parameters, "@ExcludeIds", excludeIds);

            var result = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
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
