using System.Collections.Concurrent;
using Valtuutus.Core;
using Valtuutus.Core.Data;
using Valtuutus.Core.Engines.LookupEntity;
using Valtuutus.Core.Observability;
using Valtuutus.Core.Pools;
using Valtuutus.Data.SqlServer.Utils;
using Valtuutus.Data.Db;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Valtuutus.Data.SqlServer;

public class SqlServerDataReaderProvider : RateLimiterExecuter, IDataReaderProvider
{
    private readonly DbConnectionFactory _connectionFactory;

    private static readonly ConcurrentDictionary<DbQueryCacheKey, ReaderQueries> QueryCache = new();
    private readonly ReaderQueries _q;

    public SqlServerDataReaderProvider(DbConnectionFactory connectionFactory,
        ValtuutusDataOptions options,
        IValtuutusDbOptions dbOptions) : base(options)
    {
        _connectionFactory = connectionFactory;
        _q = QueryCache.GetOrAdd(DbQueryCacheKey.From(dbOptions), static key => BuildQueries(key));
    }

    private static ReaderQueries BuildQueries(DbQueryCacheKey key)
    {
        var relationsTable = $"[{key.Schema}].[{key.RelationsTable}]";
        var attributesTable = $"[{key.Schema}].[{key.AttributesTable}]";
        const string snapPredicate = "created_tx_id <= @SnapToken AND (deleted_tx_id IS NULL OR deleted_tx_id > @SnapToken)";

        return new ReaderQueries
        {
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
            SelectAttributesByEntityAttributeFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute = @Attribute
                  AND (@EntityId IS NULL OR entity_id = @EntityId)
                """,
            SelectAttributesByEntityAttributesFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND (@EntityId IS NULL OR entity_id = @EntityId)
                  AND entity_type = @EntityType
                  AND attribute IN (SELECT id FROM @Attributes)
                """,
            SelectAttributesWithEntityIdsByAttributeFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute = @Attribute
                  AND entity_id IN (SELECT id FROM @EntityIds)
                """,
            SelectAttributesWithEntityIdsByEntityAttributesFilter = $"""
                SELECT entity_type, entity_id, attribute, value
                FROM {attributesTable}
                WHERE {snapPredicate}
                  AND entity_type = @EntityType
                  AND attribute IN (SELECT id FROM @Attributes)
                  AND entity_id IN (SELECT id FROM @EntityIds)
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
            HasDirectRelation = $"""
                SELECT CASE WHEN EXISTS(
                    SELECT 1 FROM {relationsTable}
                    WHERE {snapPredicate}
                      AND entity_type = @EntityType AND entity_id = @EntityId AND relation = @Relation
                      AND subject_id = @SubjectId AND subject_relation = ''
                ) THEN 1 ELSE 0 END
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
                      AND entity_type = @EntityType AND entity_id IN @EntityIds AND relation = @Relation
                      AND subject_id = @SubjectId AND subject_relation = ''
                ) THEN 1 ELSE 0 END
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
                ) THEN 1 ELSE 0 END
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
            GetAttributesDictScoped = $"""
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
                  AND a.attribute IN @Attributes
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

    private sealed record ReaderQueries
    {
        public required string TvpListIdsTypeName { get; init; }
        public required string GetLatestSnapToken { get; init; }
        public required string GetAttribute { get; init; }
        public required string GetAttributeWithEntityId { get; init; }
        public required string SelectAttributesByEntityAttributeFilter { get; init; }
        public required string SelectAttributesByEntityAttributesFilter { get; init; }
        public required string SelectAttributesWithEntityIdsByAttributeFilter { get; init; }
        public required string SelectAttributesWithEntityIdsByEntityAttributesFilter { get; init; }
        public required string SelectRelationsByTupleFilter { get; init; }
        public required string HasDirectRelation { get; init; }
        public required string GetIndirectRelations { get; init; }
        public required string HasAnyDirectRelation { get; init; }
        public required string SelectRelationsWithEntityIds { get; init; }
        public required string GetRelationsWithSingleSubjectSnap { get; init; }
        public required string GetRelationsWithMultiSubjectSnap { get; init; }
        public required string GetRelationsJoined { get; init; }
        public required string HasTupleToUserSetRelation { get; init; }
        public required string GetRelationsWithSingleSubjectScoped { get; init; }
        public required string GetRelationsWithMultiSubjectScoped { get; init; }
        public required string GetRelationsJoinedScoped { get; init; }
        public required string GetAttributesDictScoped { get; init; }
        public required string GetEntityIdsExcluding { get; init; }
        public required string GetSubjectIdsExcluding { get; init; }
    }

    public async Task<AttributeTuple?> GetAttribute(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var hasEntityId = !string.IsNullOrWhiteSpace(filter.EntityId);

            if (hasEntityId)
            {
                return await connection.QuerySingleOrDefaultAsync<AttributeTuple>(new CommandDefinition(
                    _q.GetAttributeWithEntityId,
                    new
                    {
                        EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                        Attribute = new DbString { Value = filter.Attribute, Length = 64 },
                        EntityId = new DbString { Value = filter.EntityId, Length = 64 },
                        SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken));
            }

            return await connection.QuerySingleOrDefaultAsync<AttributeTuple>(new CommandDefinition(
                _q.GetAttribute,
                new
                {
                    EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                    Attribute = new DbString { Value = filter.Attribute, Length = 64 },
                    SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true }
                },
                cancellationToken: cancellationToken));
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<AttributeTuple>> GetAttributes(EntityAttributeFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(
                    _q.SelectAttributesByEntityAttributeFilter,
                    new
                    {
                        EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                        Attribute = new DbString { Value = filter.Attribute, Length = 64 },
                        EntityId = new DbString { Value = filter.EntityId, Length = 64 },
                        SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken)))
                .ToList();
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<Dictionary<(string AttributeName, string EntityId), AttributeTuple>> GetAttributes(EntityAttributesFilter filter, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            if (scope is { } s)
            {
                return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(
                        _q.GetAttributesDictScoped,
                        new
                        {
                            EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                            Attributes = filter.Attributes,
                            SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true },
                            ScopeRelation = new DbString { Value = s.Relation, Length = 64 },
                            ScopeSubjectType = new DbString { Value = s.SubjectType, Length = 256 },
                            ScopeSubjectId = new DbString { Value = s.SubjectId, Length = 64 },
                        },
                        cancellationToken: cancellationToken)))
                    .ToDictionary(x => (x.Attribute, x.EntityId));
            }

            var attributesTvp = TvpHelper.AsTvpParameter(filter.Attributes, _q.TvpListIdsTypeName);
            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(
                    _q.SelectAttributesByEntityAttributesFilter,
                    new
                    {
                        EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                        EntityId = new DbString { Value = filter.EntityId, Length = 64 },
                        Attributes = attributesTvp,
                        SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<AttributeTuple>> GetAttributesSingleEntity(EntityAttributesFilter filter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var attributesTvp = TvpHelper.AsTvpParameter(filter.Attributes, _q.TvpListIdsTypeName);

            var pooled = PooledList<AttributeTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<AttributeTuple>(new CommandDefinition(
                    _q.SelectAttributesByEntityAttributesFilter,
                    new
                    {
                        EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                        EntityId = new DbString { Value = filter.EntityId, Length = 64 },
                        Attributes = attributesTvp,
                        SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken)));
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
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var entityIdsTvp = TvpHelper.AsTvpParameter(entitiesIds, _q.TvpListIdsTypeName);

            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(
                    _q.SelectAttributesWithEntityIdsByAttributeFilter,
                    new
                    {
                        EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                        Attribute = new DbString { Value = filter.Attribute, Length = 64 },
                        EntityIds = entityIdsTvp,
                        SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken)))
                .ToList();
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
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var attributesTvp = TvpHelper.AsTvpParameter(filter.Attributes, _q.TvpListIdsTypeName);
            var entityIdsTvp = TvpHelper.AsTvpParameter(entitiesIds, _q.TvpListIdsTypeName);

            return (await connection.QueryAsync<AttributeTuple>(new CommandDefinition(
                    _q.SelectAttributesWithEntityIdsByEntityAttributesFilter,
                    new
                    {
                        EntityType = new DbString { Value = filter.EntityType, Length = 256 },
                        Attributes = attributesTvp,
                        EntityIds = entityIdsTvp,
                        SnapToken = new DbString { Value = filter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken)))
                .ToDictionary(x => (x.Attribute, x.EntityId));
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<SnapToken?> GetLatestSnapToken(CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var res = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(_q.GetLatestSnapToken, cancellationToken: cancellationToken));
            return res != null ? new SnapToken(res) : (SnapToken?)null;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                    _q.SelectRelationsByTupleFilter,
                    new
                    {
                        EntityType = new DbString { Value = tupleFilter.EntityType, Length = 256 },
                        EntityId = new DbString { Value = tupleFilter.EntityId, Length = 64 },
                        Relation = new DbString { Value = tupleFilter.Relation, Length = 64 },
                        SubjectId = new DbString { Value = tupleFilter.SubjectId, Length = 64 },
                        SubjectRelation = new DbString { Value = tupleFilter.SubjectRelation, Length = 64 },
                        SubjectType = new DbString { Value = tupleFilter.SubjectType, Length = 256 },
                        SnapToken = new DbString { Value = tupleFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken)));
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

    public async Task<bool> HasDirectRelation(RelationTupleFilter tupleFilter, string subjectId, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                _q.HasDirectRelation,
                new
                {
                    EntityType = new DbString { Value = tupleFilter.EntityType, Length = 256 },
                    EntityId = new DbString { Value = tupleFilter.EntityId, Length = 64 },
                    Relation = new DbString { Value = tupleFilter.Relation, Length = 64 },
                    SubjectId = new DbString { Value = subjectId, Length = 64 },
                    SnapToken = new DbString { Value = tupleFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                },
                cancellationToken: cancellationToken)) == 1;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetIndirectRelations(RelationTupleFilter tupleFilter, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                    _q.GetIndirectRelations,
                    new
                    {
                        EntityType = new DbString { Value = tupleFilter.EntityType, Length = 256 },
                        EntityId = new DbString { Value = tupleFilter.EntityId, Length = 64 },
                        Relation = new DbString { Value = tupleFilter.Relation, Length = 64 },
                        SnapToken = new DbString { Value = tupleFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken)));
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

    public async Task<bool> HasAnyDirectRelation(string entityType, string[] entityIds, string relation,
        string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                _q.HasAnyDirectRelation,
                new
                {
                    EntityType = new DbString { Value = entityType, Length = 256 },
                    EntityIds = entityIds,
                    Relation = new DbString { Value = relation, Length = 64 },
                    SubjectId = new DbString { Value = subjectId, Length = 64 },
                    SnapToken = new DbString { Value = snapToken.Value, Length = 26, IsFixedLength = true }
                },
                cancellationToken: cancellationToken)) == 1;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<PooledList<RelationTuple>> GetRelationsWithEntityIds(EntityRelationFilter entityRelationFilter, string subjectType, IEnumerable<string> entityIds, string? subjectRelation, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var entityIdsTvp = TvpHelper.AsTvpParameter(entityIds, _q.TvpListIdsTypeName);

            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                    _q.SelectRelationsWithEntityIds,
                    new
                    {
                        SubjectType = new DbString { Value = subjectType, Length = 256 },
                        EntityType = new DbString { Value = entityRelationFilter.EntityType, Length = 256 },
                        Relation = new DbString { Value = entityRelationFilter.Relation, Length = 64 },
                        EntityIds = entityIdsTvp,
                        SubjectRelation = new DbString { Value = subjectRelation, Length = 64 },
                        SnapToken = new DbString { Value = entityRelationFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                    },
                    cancellationToken: cancellationToken)));
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
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            if (subjectsIds.Length == 1)
            {
                var pooled = PooledList<RelationTuple>.Rent();
                try
                {
                    if (scope is { } s)
                    {
                        pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                            _q.GetRelationsWithSingleSubjectScoped,
                            new
                            {
                                EntityType = new DbString { Value = entityFilter.EntityType, Length = 256 },
                                Relation = new DbString { Value = entityFilter.Relation, Length = 64 },
                                SubjectType = new DbString { Value = subjectType, Length = 256 },
                                SubjectId = new DbString { Value = subjectsIds[0], Length = 64 },
                                SnapToken = new DbString { Value = entityFilter.SnapToken.Value, Length = 26, IsFixedLength = true },
                                ScopeRelation = new DbString { Value = s.Relation, Length = 64 },
                                ScopeSubjectType = new DbString { Value = s.SubjectType, Length = 256 },
                                ScopeSubjectId = new DbString { Value = s.SubjectId, Length = 64 },
                            },
                            cancellationToken: cancellationToken)));
                    }
                    else
                    {
                        pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                            _q.GetRelationsWithSingleSubjectSnap,
                            new
                            {
                                EntityType = new DbString { Value = entityFilter.EntityType, Length = 256 },
                                Relation = new DbString { Value = entityFilter.Relation, Length = 64 },
                                SubjectType = new DbString { Value = subjectType, Length = 256 },
                                SubjectId = new DbString { Value = subjectsIds[0], Length = 64 },
                                SnapToken = new DbString { Value = entityFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                            },
                            cancellationToken: cancellationToken)));
                    }
                    return pooled;
                }
                catch
                {
                    pooled.Dispose();
                    throw;
                }
            }

            var subjectsTvp = TvpHelper.AsTvpParameter(subjectsIds, _q.TvpListIdsTypeName);
            var multi = PooledList<RelationTuple>.Rent();
            try
            {
                if (scope is { } ms)
                {
                    multi.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                        _q.GetRelationsWithMultiSubjectScoped,
                        new
                        {
                            EntityType = new DbString { Value = entityFilter.EntityType, Length = 256 },
                            Relation = new DbString { Value = entityFilter.Relation, Length = 64 },
                            SubjectType = new DbString { Value = subjectType, Length = 256 },
                            SubjectIds = subjectsTvp,
                            SnapToken = new DbString { Value = entityFilter.SnapToken.Value, Length = 26, IsFixedLength = true },
                            ScopeRelation = new DbString { Value = ms.Relation, Length = 64 },
                            ScopeSubjectType = new DbString { Value = ms.SubjectType, Length = 256 },
                            ScopeSubjectId = new DbString { Value = ms.SubjectId, Length = 64 },
                        },
                        cancellationToken: cancellationToken)));
                }
                else
                {
                    multi.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                        _q.GetRelationsWithMultiSubjectSnap,
                        new
                        {
                            EntityType = new DbString { Value = entityFilter.EntityType, Length = 256 },
                            Relation = new DbString { Value = entityFilter.Relation, Length = 64 },
                            SubjectType = new DbString { Value = subjectType, Length = 256 },
                            SubjectIds = subjectsTvp,
                            SnapToken = new DbString { Value = entityFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                        },
                        cancellationToken: cancellationToken)));
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

    public async Task<PooledList<RelationTuple>> GetRelationsJoined(
        EntityRelationFilter mainFilter, string subEntityType, string subRelation,
        string subjectType, string subjectId, EntityScope? scope, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var pooled = PooledList<RelationTuple>.Rent();
            try
            {
                if (scope is { } s)
                {
                    pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                        _q.GetRelationsJoinedScoped,
                        new
                        {
                            EntityType = new DbString { Value = mainFilter.EntityType, Length = 256 },
                            Relation = new DbString { Value = mainFilter.Relation, Length = 64 },
                            SubEntityType = new DbString { Value = subEntityType, Length = 256 },
                            SubRelation = new DbString { Value = subRelation, Length = 64 },
                            SubjectType = new DbString { Value = subjectType, Length = 256 },
                            SubjectId = new DbString { Value = subjectId, Length = 64 },
                            SnapToken = new DbString { Value = mainFilter.SnapToken.Value, Length = 26, IsFixedLength = true },
                            ScopeRelation = new DbString { Value = s.Relation, Length = 64 },
                            ScopeSubjectType = new DbString { Value = s.SubjectType, Length = 256 },
                            ScopeSubjectId = new DbString { Value = s.SubjectId, Length = 64 },
                        },
                        cancellationToken: cancellationToken)));
                }
                else
                {
                    pooled.AddRange(await connection.QueryAsync<RelationTuple>(new CommandDefinition(
                        _q.GetRelationsJoined,
                        new
                        {
                            EntityType = new DbString { Value = mainFilter.EntityType, Length = 256 },
                            Relation = new DbString { Value = mainFilter.Relation, Length = 64 },
                            SubEntityType = new DbString { Value = subEntityType, Length = 256 },
                            SubRelation = new DbString { Value = subRelation, Length = 64 },
                            SubjectType = new DbString { Value = subjectType, Length = 256 },
                            SubjectId = new DbString { Value = subjectId, Length = 64 },
                            SnapToken = new DbString { Value = mainFilter.SnapToken.Value, Length = 26, IsFixedLength = true }
                        },
                        cancellationToken: cancellationToken)));
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

    public async Task<bool> HasTupleToUserSetRelation(
        string entityType, string entityId, string tupleSetRelation,
        string subEntityType, string computedRelation,
        string subjectType, string subjectId, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();
            var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                _q.HasTupleToUserSetRelation,
                new
                {
                    EntityType = new DbString { Value = entityType, Length = 256 },
                    EntityId = new DbString { Value = entityId, Length = 64 },
                    TupleSetRelation = new DbString { Value = tupleSetRelation, Length = 64 },
                    ComputedRelation = new DbString { Value = computedRelation, Length = 64 },
                    SubjectType = new DbString { Value = subjectType, Length = 256 },
                    SubjectId = new DbString { Value = subjectId, Length = 64 },
                    SnapToken = new DbString { Value = snapToken.Value, Length = 26, IsFixedLength = true }
                },
                cancellationToken: cancellationToken));
            return result == 1;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetEntityIdsExcluding(string entityType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            var excludeIdsParam = TvpHelper.AsTvpParameter(excludeIds, _q.TvpListIdsTypeName);
            var rows = await connection.QueryAsync<string>(new CommandDefinition(
                _q.GetEntityIdsExcluding,
                new
                {
                    EntityType = new DbString { Value = entityType, Length = 256 },
                    SnapToken = new DbString { Value = snapToken.Value, Length = 26, IsFixedLength = true },
                    ExcludeIds = excludeIdsParam
                },
                cancellationToken: cancellationToken));
            return rows is List<string> list ? list : rows.ToList();
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<List<string>> GetSubjectIdsExcluding(string subjectType, IReadOnlyCollection<string> excludeIds, SnapToken snapToken, CancellationToken cancellationToken)
    {
        using var activity = DefaultActivitySource.Instance.StartActivity();
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var connection = (SqlConnection)_connectionFactory();

            var excludeIdsParam = TvpHelper.AsTvpParameter(excludeIds, _q.TvpListIdsTypeName);
            var rows = await connection.QueryAsync<string>(new CommandDefinition(
                _q.GetSubjectIdsExcluding,
                new
                {
                    SubjectType = new DbString { Value = subjectType, Length = 256 },
                    SnapToken = new DbString { Value = snapToken.Value, Length = 26, IsFixedLength = true },
                    ExcludeIds = excludeIdsParam
                },
                cancellationToken: cancellationToken));
            return rows is List<string> list ? list : rows.ToList();
        }
        finally
        {
            Semaphore.Release();
        }
    }
}
